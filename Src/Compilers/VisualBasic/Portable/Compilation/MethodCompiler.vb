' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Immutable
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.CodeAnalysis.CodeGen
Imports Microsoft.CodeAnalysis.Diagnostics
Imports Microsoft.CodeAnalysis.Emit
Imports Microsoft.CodeAnalysis.Instrumentation
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Emit
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic

    Friend NotInheritable Class MethodCompiler
        Inherits VisualBasicSymbolVisitor

        Private ReadOnly _compilation As VisualBasicCompilation
        Private ReadOnly _cancellationToken As CancellationToken
        Private ReadOnly _generateDebugInfo As Boolean
        Private ReadOnly _diagnostics As DiagnosticBag
        Private ReadOnly _hasDeclarationErrors As Boolean
        Private ReadOnly _namespaceScopeBuilder As NamespaceScopeBuilder
        Private ReadOnly _moduleBeingBuiltOpt As PEModuleBuilder ' Nothing if compiling for diagnostics
        Private ReadOnly _filterOpt As Predicate(Of Symbol)      ' If not Nothing, limit analysis to specific symbols
        Private ReadOnly _debugDocumentProvider As DebugDocumentProvider

        ' GetDiagnostics only needs to Bind. If we need to go further, _doEmitPhase needs to be set. 
        ' It normally happens during actual compile, but also happens when getting emit diagnostics for 
        ' testing purposes.
        Private ReadOnly _doEmitPhase As Boolean

        ' MethodCompiler employs concurrency by following flattened fork/join pattern.
        '
        ' For every item that we want to compile in parallel a new task is forked.
        ' compileTaskQueue is used to track and observe all the tasks. 
        ' Once compileTaskQueue is empty, we know that there are no more tasks (and no more can be created)
        ' and that means we are done compiling. WaitForWorkers ensures this condition.
        '
        ' Note that while tasks may fork more tasks (nested types, lambdas, whatever else that may introduce more types),
        ' we do not want any child/parent relationship between spawned tasks and their creators. 
        ' Creator has no real dependencies on the completion of its children and should finish and release any resources
        ' as soon as it can regardless of the tasks it may have spawned.
        '
        ' Stack is used so that the wait would observe the most recently added task and have 
        ' more chances to do inlined execution.
        Private compilerTasks As ConcurrentStack(Of Task)

        ' Tracks whether any method body has hasErrors set, and used to avoid
        ' emitting if there are errors without corresponding diagnostics.
        ' NOTE: once the flag is set to true, it should never go back to false!!!
        Private _globalHasErrors As Boolean

        Private ReadOnly Property GlobalHasErrors As Boolean
            Get
                Return _globalHasErrors
            End Get
        End Property

        Private Sub SetGlobalErrorIfTrue(arg As Boolean)
            ' NOTE: this is not a volatile write
            '       for correctness we need only single threaded consistency.
            '       Within a single task - if we have got an error it may not be safe to continue with some lowerings.
            '       It is ok if other tasks will see the change after some delay or does not observe at all.
            '       Such races are unavoidable and will just result in performing some work that is safe to do
            '       but may no longer be needed.
            '       The final Join of compiling tasks cannot happen without interlocked operations and that 
            '       will that the final state of the flag is synchronized after we are done compiling.
            If arg Then
                _globalHasErrors = True
            End If
        End Sub


        ' moduleBeingBuilt can be Nothing in order to just analyze methods for errors.
        Private Sub New(compilation As VisualBasicCompilation,
                       moduleBeingBuiltOpt As PEModuleBuilder,
                       generateDebugInfo As Boolean,
                       doEmitPhase As Boolean,
                       hasDeclarationErrors As Boolean,
                       diagnostics As DiagnosticBag,
                       filter As Predicate(Of Symbol),
                       cancellationToken As CancellationToken)

            Me._compilation = compilation
            Me._moduleBeingBuiltOpt = moduleBeingBuiltOpt
            Me._diagnostics = diagnostics
            Me._hasDeclarationErrors = hasDeclarationErrors
            Me._cancellationToken = cancellationToken
            Me._doEmitPhase = doEmitPhase
            Me._generateDebugInfo = generateDebugInfo
            Me._filterOpt = filter

            If generateDebugInfo Then
                Me._debugDocumentProvider = Function(path As String, basePath As String) moduleBeingBuiltOpt.GetOrAddDebugDocument(path, basePath, AddressOf CreateDebugDocumentForFile)
                Me._namespaceScopeBuilder = New NamespaceScopeBuilder()
            End If

            If compilation.Options.ConcurrentBuild Then
                Me.compilerTasks = New ConcurrentStack(Of Task)()
            End If
        End Sub

        Private Shared Function IsDefinedOrImplementedInSourceTree(symbol As Symbol, tree As SyntaxTree, span As TextSpan?) As Boolean
            If symbol.IsDefinedInSourceTree(tree, span) Then
                Return True
            End If

            Dim method = TryCast(symbol, SourceMemberMethodSymbol)
            If method IsNot Nothing AndAlso
                method.IsPartialDefinition Then

                Dim implementationPart = method.PartialImplementationPart
                If implementationPart IsNot Nothing Then
                    Return implementationPart.IsDefinedInSourceTree(tree, span)
                End If
            End If

            Return False
        End Function

        ''' <summary>
        ''' Completes binding and performs analysis of bound trees for the purpose of obtaining diagnostics.
        ''' 
        ''' NOTE: This method does not perform lowering/rewriting/emit. 
        '''       Errors from those stages require complete compile, 
        '''       but generally are not interesting during editing.
        ''' 
        ''' NOTE: the bound tree produced by this method are not stored anywhere
        '''       and immediately lost after diagnostics of a particular tree is done.
        '''       
        ''' </summary>
        Public Shared Sub GetCompileDiagnostics(compilation As VisualBasicCompilation,
                                                root As NamespaceSymbol,
                                                tree As SyntaxTree,
                                                filterSpanWithinTree As TextSpan?,
                                                hasDeclarationErrors As Boolean,
                                                diagnostics As DiagnosticBag,
                                                doEmitPhase As Boolean,
                                                cancellationToken As CancellationToken)

            Dim filter As Predicate(Of Symbol) = Nothing

            If tree IsNot Nothing Then
                filter = Function(sym) IsDefinedOrImplementedInSourceTree(sym, tree, filterSpanWithinTree)
            End If

            Dim compiler = New MethodCompiler(compilation,
                                              moduleBeingBuiltOpt:=Nothing,
                                              generateDebugInfo:=False,
                                              doEmitPhase:=doEmitPhase,
                                              hasDeclarationErrors:=hasDeclarationErrors,
                                              diagnostics:=diagnostics,
                                              filter:=filter,
                                              cancellationToken:=cancellationToken)

            root.Accept(compiler)

            If tree Is Nothing Then
                ' include entry point diagnostics if we are compiling the entire compilation, not just a single tree:
                Dim entryPointAndDiagnostics = compilation.GetEntryPointAndDiagnostics(cancellationToken)
                If entryPointAndDiagnostics IsNot Nothing Then
                    diagnostics.AddRange(entryPointAndDiagnostics.Diagnostics)
                End If
            End If

            compiler.WaitForWorkers()
        End Sub

        ''' <summary>
        ''' Compiles given compilation into provided module.
        ''' 
        ''' NOTE: it is ok for moduleBeingBuiltOpt to be Nothing. 
        '''       In such case the only results of this method would be diagnostics for complete compile.
        ''' 
        ''' NOTE: the bound/lowered trees produced by this method are not stored anywhere and
        '''       immediately lost after obtaining method bodies and diagnostics for a particular
        '''       tree.
        ''' </summary>
        Friend Shared Sub CompileMethodBodies(compilation As VisualBasicCompilation,
                                              moduleBeingBuiltOpt As PEModuleBuilder,
                                              generateDebugInfo As Boolean,
                                              hasDeclarationErrors As Boolean,
                                              filter As Predicate(Of Symbol),
                                              diagnostics As DiagnosticBag,
                                              Optional cancellationToken As CancellationToken = Nothing)

            Using Logger.LogBlock(FunctionId.VisualBasic_Compiler_CompileMethodBodies, message:=compilation.AssemblyName, cancellationToken:=cancellationToken)
                If compilation.PreviousSubmission IsNot Nothing Then
                    ' In case there is a previous submission, we should ensure 
                    ' it has already created anonymous type/delegates templates

                    ' NOTE: if there are any errors, we will pick up what was created anyway
                    compilation.PreviousSubmission.EnsureAnonymousTypeTemplates(cancellationToken)

                    ' TODO: revise to use a loop instead of a recursion
                End If

#If DEBUG Then
                compilation.EmbeddedSymbolManager.AssertMarkAllDeferredSymbolsAsReferencedIsCalled()
#End If

                Dim compiler = New MethodCompiler(compilation,
                                                  moduleBeingBuiltOpt,
                                                  generateDebugInfo,
                                                  doEmitPhase:=True,
                                                  hasDeclarationErrors:=hasDeclarationErrors,
                                                  diagnostics:=diagnostics,
                                                  filter:=filter,
                                                  cancellationToken:=cancellationToken)

                compilation.SourceModule.GlobalNamespace.Accept(compiler)
                compiler.WaitForWorkers()

                If moduleBeingBuiltOpt IsNot Nothing Then
                    Dim additionalTypes = moduleBeingBuiltOpt.GetAdditionalTopLevelTypes()
                    If Not additionalTypes.IsEmpty Then
                        compiler.CompileSynthesizedMethods(additionalTypes)
                    End If

                    compilation.AnonymousTypeManager.AssignTemplatesNamesAndCompile(compiler, moduleBeingBuiltOpt, diagnostics)
                    compiler.WaitForWorkers()

                    ' Process symbols from embedded code if needed.
                    If compilation.EmbeddedSymbolManager.Embedded <> EmbeddedSymbolKind.None Then
                        compiler.ProcessEmbeddedMethods()
                    End If

                    Dim privateImplClass = moduleBeingBuiltOpt.PrivateImplClass
                    If privateImplClass IsNot Nothing Then
                        ' all threads that were adding methods must be finished now, we can freeze the class:
                        privateImplClass.Freeze()

                        compiler.CompileSynthesizedMethods(privateImplClass)
                    End If
                End If

                Dim entryPoint = GetEntryPoint(compilation, moduleBeingBuiltOpt, diagnostics, cancellationToken)
                If moduleBeingBuiltOpt IsNot Nothing Then
                    moduleBeingBuiltOpt.SetEntryPoint(entryPoint)

                    If compiler.GlobalHasErrors AndAlso Not hasDeclarationErrors AndAlso Not diagnostics.HasAnyErrors Then
                        ' If there were errors but no diagnostics, explicitly add
                        ' a "Failed to emit module" error to prevent emitting.
                        diagnostics.Add(ERRID.ERR_ModuleEmitFailure, NoLocation.Singleton, moduleBeingBuiltOpt.SourceModule.Name)
                    End If
                End If
            End Using
        End Sub

        Friend Shared Function GetEntryPoint(compilation As VisualBasicCompilation,
                                             moduleBeingBuilt As PEModuleBuilder,
                                             diagnostics As DiagnosticBag,
                                             cancellationToken As CancellationToken) As MethodSymbol

            Dim options As VisualBasicCompilationOptions = compilation.Options
            If Not options.OutputKind.IsApplication() Then
                Debug.Assert(compilation.GetEntryPointAndDiagnostics(Nothing) Is Nothing)

                If compilation.IsSubmission Then
                    Dim submissionReturnType As TypeSymbol = compilation.GetSubmissionReturnType()
                    Return DefineScriptEntryPoint(compilation, moduleBeingBuilt, submissionReturnType, diagnostics)
                End If

                Return Nothing
            End If

            Debug.Assert(Not compilation.IsSubmission)
            Debug.Assert(options.OutputKind.IsApplication())

            Dim entryPointAndDiagnostics = compilation.GetEntryPointAndDiagnostics(cancellationToken)
            Debug.Assert(entryPointAndDiagnostics IsNot Nothing)
            Debug.Assert(Not entryPointAndDiagnostics.Diagnostics.IsDefault)

            diagnostics.AddRange(entryPointAndDiagnostics.Diagnostics)

            If compilation.ScriptClass IsNot Nothing Then
                Debug.Assert(entryPointAndDiagnostics.MethodSymbol Is Nothing)
                Return DefineScriptEntryPoint(compilation, moduleBeingBuilt, compilation.GetSpecialType(SpecialType.System_Void), diagnostics)
            End If

            Debug.Assert(entryPointAndDiagnostics.MethodSymbol IsNot Nothing OrElse entryPointAndDiagnostics.Diagnostics.HasAnyErrors() OrElse Not compilation.Options.Errors.IsDefaultOrEmpty)
            Return entryPointAndDiagnostics.MethodSymbol
        End Function

        Friend Shared Function DefineScriptEntryPoint(compilation As VisualBasicCompilation, moduleBeingBuilt As PEModuleBuilder, returnType As TypeSymbol, diagnostics As DiagnosticBag) As MethodSymbol
            Dim scriptEntryPoint = New SynthesizedEntryPointSymbol(compilation.ScriptClass, returnType)
            If moduleBeingBuilt IsNot Nothing AndAlso Not diagnostics.HasAnyErrors Then
                Dim compilationState = New TypeCompilationState(compilation, moduleBeingBuilt, initializeComponentOpt:=Nothing)
                Dim body = scriptEntryPoint.CreateBody()

                Dim emittedBody = GenerateMethodBody(moduleBeingBuilt,
                                                     scriptEntryPoint,
                                                     body,
                                                     variableSlotAllocatorOpt:=Nothing,
                                                     debugDocumentProvider:=Nothing,
                                                     diagnostics:=diagnostics,
                                                     namespaceScopes:=Nothing)

                moduleBeingBuilt.SetMethodBody(scriptEntryPoint, emittedBody)
                moduleBeingBuilt.AddSynthesizedDefinition(compilation.ScriptClass, scriptEntryPoint)
            End If

            Return scriptEntryPoint
        End Function

        Private Sub WaitForWorkers()
            Dim tasks As ConcurrentStack(Of Task) = Me.compilerTasks
            If tasks Is Nothing Then
                Return
            End If

            Dim curTask As Task = Nothing
            While tasks.TryPop(curTask)
                curTask.GetAwaiter().GetResult()
            End While
        End Sub

#Region "Embedded symbols processing"

        Private Sub ProcessEmbeddedMethods()
            Dim manager = _compilation.EmbeddedSymbolManager
            Dim processedSymbols As New ConcurrentSet(Of Symbol)(ReferenceEqualityComparer.Instance)

            Dim builder = ArrayBuilder(Of Symbol).GetInstance
            Do
                ' We iterate all the symbols for embedded code that are CURRENTLY known
                ' to be referenced by user code or already emitted embedded code. 
                '
                ' If this the first full emit of the current compilation and there are no 
                ' concurrent emits this collection will consist of embedded symbols referenced 
                ' from attributes on compilation source symbols (by directly using embedded 
                ' attributes OR referencing embedded types from attributes' arguments via
                ' 'GetType(EmbeddedTypeName)' expressions).
                ' 
                ' The consecutive iterations may also see new embedded symbols referenced
                ' as we compile and emit embedded methods.
                '
                ' If there are concurrent emits in place more referenced symbols may be returned 
                ' by GetCurrentReferencedSymbolsSnapshot than in simple case described above,
                ' thus reducing the number of iterations of the outer Do loop.
                '
                ' Note that GetCurrentReferencedSymbolsSnapshot actually makes a snapshot
                ' of the referenced symbols.
                manager.GetCurrentReferencedSymbolsSnapshot(builder, processedSymbols)
                If builder.Count = 0 Then
                    Exit Do
                End If

                For index = 0 To builder.Count - 1
                    Dim symbol As symbol = builder(index)
                    processedSymbols.Add(symbol)

#If DEBUG Then
                    ' In DEBUG assert that the type does not have 
                    ' field initializers except those in const fields
                    If symbol.Kind = SymbolKind.NamedType Then
                        Dim embeddedType = DirectCast(symbol, EmbeddedSymbolManager.EmbeddedNamedTypeSymbol)
                        AssertAllInitializersAreConstants(embeddedType.StaticInitializers)
                        AssertAllInitializersAreConstants(embeddedType.InstanceInitializers)
                    End If
#End If
                    ' Compile method
                    If symbol.Kind = SymbolKind.Method Then
                        Dim embeddedMethod = DirectCast(symbol, MethodSymbol)
                        EmbeddedSymbolManager.ValidateMethod(embeddedMethod)
                        VisitEmbeddedMethod(embeddedMethod)
                    End If
                Next

                builder.Clear()
            Loop
            builder.Free()

            ' Seal the referenced symbol collection
            manager.SealCollection()
        End Sub

        Private Sub VisitEmbeddedMethod(method As MethodSymbol)
            ' Lazily created collection of synthetic methods which 
            ' may be created during compilation of methods
            Dim compilationState As TypeCompilationState = New TypeCompilationState(_compilation, _moduleBeingBuiltOpt, initializeComponentOpt:=Nothing)

            ' Containing type binder

            ' NOTE: we need to provide type binder for the constructor compilation,
            '       so we create it for each constructor, but it does not seem to be a 
            '       problem since current embedded types have only one constructor per 
            '       type; this might need to be revised later if this assumption changes
            Dim sourceTypeBinder As Binder = If(method.MethodKind = MethodKind.Ordinary, Nothing,
                                                    BinderBuilder.CreateBinderForType(
                                                        DirectCast(method.ContainingModule, SourceModuleSymbol),
                                                        method.ContainingType.Locations(0).PossiblyEmbeddedOrMySourceTree(),
                                                        method.ContainingType))


            Dim referencedConstructor As MethodSymbol = Nothing
            CompileMethod(method,
                          filter:=Nothing,
                          compilationState:=compilationState,
                          processedInitializers:=Binder.ProcessedFieldOrPropertyInitializers.Empty,
                          containingTypeBinder:=sourceTypeBinder,
                          previousSubmissionFields:=Nothing,
                          referencedConstructor:=referencedConstructor)

            ' Do not expect constructor --> constructor calls for embedded types
            Debug.Assert(referencedConstructor Is Nothing OrElse
                         Not referencedConstructor.ContainingType.Equals(method.ContainingType))

            ' Don't expect any synthetic methods created for embedded types
            Debug.Assert(Not compilationState.HasSynthesizedMethods)
        End Sub

        <Conditional("DEBUG")>
        Private Sub AssertAllInitializersAreConstants(initializers As ImmutableArray(Of ImmutableArray(Of FieldOrPropertyInitializer)))
            If Not initializers.IsDefaultOrEmpty Then
                For Each initializerGroup In initializers
                    If Not initializerGroup.IsEmpty Then
                        For Each initializer In initializerGroup
                            For Each fieldOrProperty In initializer.FieldsOrProperty
                                Debug.Assert(fieldOrProperty.Kind = SymbolKind.Field)
                                Debug.Assert(DirectCast(fieldOrProperty, FieldSymbol).IsConst)
                            Next
                        Next
                    End If
                Next
            End If
        End Sub

#End Region

        Private ReadOnly Property DoEmitPhase() As Boolean
            Get
                Return _doEmitPhase
            End Get
        End Property

        Public Overrides Sub VisitNamespace(symbol As NamespaceSymbol)
            _cancellationToken.ThrowIfCancellationRequested()

            If Me._compilation.Options.ConcurrentBuild Then
                Dim worker As Task = CompileNamespaceAsTask(symbol)
                compilerTasks.Push(worker)
            Else
                CompileNamespace(symbol)
            End If
        End Sub

        Private Function CompileNamespaceAsTask(symbol As NamespaceSymbol) As Task
            Return Task.Run(
                Sub()
                    Try
                        CompileNamespace(symbol)
                    Catch e As Exception When CompilerFatalError.ReportUnlessCanceled(e)
                        Throw ExceptionUtilities.Unreachable
                    End Try
                End Sub,
                Me._cancellationToken)
        End Function

        Private Sub CompileNamespace(symbol As NamespaceSymbol)
            If PassesFilter(_filterOpt, symbol) Then
                For Each member In symbol.GetMembersUnordered()
                    member.Accept(Me)
                Next
            End If
        End Sub

        Public Overrides Sub VisitNamedType(symbol As NamedTypeSymbol)
            _cancellationToken.ThrowIfCancellationRequested()
            If PassesFilter(_filterOpt, symbol) Then
                If Me._compilation.Options.ConcurrentBuild Then
                    Dim worker As Task = CompileNamedTypeAsTask(symbol, _filterOpt)
                    compilerTasks.Push(worker)
                Else
                    CompileNamedType(symbol, _filterOpt)
                End If
            End If
        End Sub

        Private Function CompileNamedTypeAsTask(symbol As NamedTypeSymbol, filter As Predicate(Of Symbol)) As Task
            Return Task.Run(
                Sub()
                    Try
                        CompileNamedType(symbol, filter)
                    Catch e As Exception When CompilerFatalError.ReportUnlessCanceled(e)
                        Throw ExceptionUtilities.Unreachable
                    End Try
                End Sub,
                Me._cancellationToken)
        End Function

        Private Sub CompileNamedType(symbol As NamedTypeSymbol, filter As Predicate(Of Symbol))
            If symbol.IsEmbedded Then
                ' Don't process embedded types
                Return
            End If

            ' Find the constructor of a script class.
            Dim scriptCtor As MethodSymbol = Nothing
            If symbol.IsScriptClass Then
                scriptCtor = symbol.InstanceConstructors(0)
                Debug.Assert(scriptCtor IsNot Nothing)
            End If

            Dim processedStaticInitializers = Binder.ProcessedFieldOrPropertyInitializers.Empty
            Dim processedInstanceInitializers = Binder.ProcessedFieldOrPropertyInitializers.Empty
            Dim synthesizedSubmissionFields = If(symbol.IsSubmissionClass, New SynthesizedSubmissionFields(_compilation, symbol), Nothing)

            ' if this is a type symbol from source we'll try to bind the field initializers as well
            Dim sourceTypeSymbol = TryCast(symbol, SourceMemberContainerTypeSymbol)
            Dim initializeComponent As MethodSymbol = Nothing

            If sourceTypeSymbol IsNot Nothing AndAlso DoEmitPhase Then
                initializeComponent = GetDesignerInitializeComponentMethod(sourceTypeSymbol)
            End If

            ' Lazily created collection of synthetic methods which 
            ' may be created during compilation of methods
            Dim compilationState As TypeCompilationState = New TypeCompilationState(_compilation, _moduleBeingBuiltOpt, initializeComponent)

            ' Containing type binder
            Dim sourceTypeBinder As Binder = Nothing

            If sourceTypeSymbol IsNot Nothing Then

                Debug.Assert(sourceTypeSymbol.Locations.Length > 0)
                sourceTypeBinder = BinderBuilder.CreateBinderForType(
                                        DirectCast(sourceTypeSymbol.ContainingModule, SourceModuleSymbol),
                                        sourceTypeSymbol.Locations(0).PossiblyEmbeddedOrMySourceTree,
                                        sourceTypeSymbol)

                Binder.BindFieldAndPropertyInitializers(sourceTypeSymbol,
                                                        sourceTypeSymbol.StaticInitializers,
                                                        scriptCtor,
                                                        processedStaticInitializers,
                                                        _diagnostics)

                Binder.BindFieldAndPropertyInitializers(sourceTypeSymbol,
                                                        sourceTypeSymbol.InstanceInitializers,
                                                        scriptCtor,
                                                        processedInstanceInitializers,
                                                        _diagnostics)

                ' TODO: any flow analysis for initializers?

                ' const fields of type date or decimal require a shared constructor. We decided that this constructor
                ' should not be part of the type's member list. If there is not already a shared constructor, we're 
                ' creating one and call CompileMethod to rewrite the field initializers. 
                Dim sharedDefaultConstructor = sourceTypeSymbol.CreateSharedConstructorsForConstFieldsIfRequired(sourceTypeBinder, _diagnostics)
                If sharedDefaultConstructor IsNot Nothing AndAlso PassesFilter(filter, sharedDefaultConstructor) Then
                    CompileMethod(sharedDefaultConstructor, filter, compilationState, processedStaticInitializers, sourceTypeBinder, synthesizedSubmissionFields)

                    If _moduleBeingBuiltOpt IsNot Nothing Then
                        _moduleBeingBuiltOpt.AddSynthesizedDefinition(sourceTypeSymbol, sharedDefaultConstructor)
                    End If
                End If
            End If

            ' Constructor --> Constructor calls to be used in cycles detection
            Dim constructorCallMap As Dictionary(Of MethodSymbol, MethodSymbol) = Nothing

            For Each member In symbol.GetMembers()
                If Not PassesFilter(filter, member) Then
                    Continue For
                End If

                ' process all members that are not methods as usual                     
                If member.Kind = SymbolKind.NamedType Then
                    member.Accept(Me)
                ElseIf member.Kind = SymbolKind.Method Then

                    Dim method = DirectCast(member, MethodSymbol)
                    If Not method.IsSubmissionConstructor Then

                        If method.IsPartial() Then
                            Dim impl = method.PartialImplementationPart
                            If impl IsNot method Then
                                If CType(method, SourceMethodSymbol).SetDiagnostics(ImmutableArray(Of Diagnostic).Empty) Then
                                    method.DeclaringCompilation.SymbolDeclaredEvent(method)
                                End If

                                If impl Is Nothing Then
                                    Continue For
                                Else
                                    method = impl
                                End If
                            End If
                        End If

                        ' pass correct processed initializers for the static or instance constructors, 
                        ' otherwise pass nothing
                        Dim processedInitializers = Binder.ProcessedFieldOrPropertyInitializers.Empty

                        If method.MethodKind = MethodKind.SharedConstructor Then
                            processedInitializers = processedStaticInitializers
                        ElseIf method.MethodKind = MethodKind.Constructor Then
                            processedInitializers = processedInstanceInitializers
                        End If

                        Dim referencedConstructor As MethodSymbol = Nothing
                        CompileMethod(method, filter, compilationState, processedInitializers, sourceTypeBinder, synthesizedSubmissionFields, referencedConstructor)

                        ' If 'referencedConstructor' is returned by 'CompileMethod', the method just compiled 
                        ' was a constructor which references the returned symbol. We might want to store 
                        ' some of those constructors in 'constructorCallMap' to process later for cycle detection
                        If referencedConstructor IsNot Nothing Then

                            '  If base class constructor is called, the constructor cannot be part of a cycle
                            If referencedConstructor.ContainingType.Equals(symbol) Then
                                If constructorCallMap Is Nothing Then
                                    constructorCallMap = New Dictionary(Of MethodSymbol, MethodSymbol)
                                End If
                                constructorCallMap.Add(method, referencedConstructor)
                            End If

                        End If

                        ' Create exact signature stubs for interface implementations.
                        If DoEmitPhase AndAlso _moduleBeingBuiltOpt IsNot Nothing Then
                            CreateExplicitInterfaceImplementationStubs(compilationState, method)
                        End If
                    End If
                End If
            Next

            Debug.Assert(symbol.TypeKind <> TypeKind.Submission OrElse (scriptCtor IsNot Nothing AndAlso scriptCtor.IsSubmissionConstructor))

            ' Detect and report cycles in constructor calls
            If constructorCallMap IsNot Nothing Then
                DetectAndReportCyclesInConstructorCalls(constructorCallMap, _diagnostics)
            End If

            ' Compile submission constructor last so that synthesized submission fields are collected from all script methods:
            If scriptCtor IsNot Nothing AndAlso scriptCtor.IsSubmissionConstructor Then
                CompileMethod(scriptCtor, filter, compilationState, processedInstanceInitializers, sourceTypeBinder, synthesizedSubmissionFields)

                If synthesizedSubmissionFields IsNot Nothing AndAlso _moduleBeingBuiltOpt IsNot Nothing Then
                    synthesizedSubmissionFields.AddToType(symbol, _moduleBeingBuiltOpt)
                End If
            End If

            ' Report warnings for constructors that do not call InitializeComponent
            If initializeComponent IsNot Nothing Then
                For Each member In symbol.GetMembers()
                    If member.IsShared OrElse Not member.IsFromCompilation(_compilation) OrElse member.Kind <> SymbolKind.Method Then
                        Continue For
                    End If

                    Dim sourceMethod = TryCast(member, SourceMemberMethodSymbol)

                    If sourceMethod IsNot Nothing AndAlso
                       sourceMethod.MethodKind = MethodKind.Constructor AndAlso
                       Not compilationState.CallsInitializeComponent(sourceMethod) Then
                        Dim location As Location = sourceMethod.NonMergedLocation
                        Debug.Assert(location IsNot Nothing)

                        If location IsNot Nothing Then
                            Binder.ReportDiagnostic(_diagnostics, location, ERRID.WRN_ExpectedInitComponentCall2, sourceMethod, sourceTypeSymbol)
                        End If
                    End If
                Next
            End If

            ' Add synthetic methods created for this type in above calls to CompileMethod
            If _moduleBeingBuiltOpt IsNot Nothing Then
                CompileSynthesizedMethods(compilationState)
            End If

            compilationState.Free()
        End Sub

        Private Sub CreateExplicitInterfaceImplementationStubs(compilationState As TypeCompilationState, method As MethodSymbol)
            ' It is not common to have signature mismatch, let's avoid any extra work
            ' and allocations until we know that we have a mismatch.
            Dim stubs As ArrayBuilder(Of SynthesizedInterfaceImplementationStubSymbol) = Nothing

            For Each implemented In method.ExplicitInterfaceImplementations
                If Not MethodSignatureComparer.CustomModifiersAndParametersAndReturnTypeSignatureComparer.Equals(method, implemented) Then
                    If stubs Is Nothing Then
                        stubs = ArrayBuilder(Of SynthesizedInterfaceImplementationStubSymbol).GetInstance()
                    End If

                    Dim matchingStub As SynthesizedInterfaceImplementationStubSymbol = Nothing

                    For Each candidate In stubs
                        If MethodSignatureComparer.CustomModifiersAndParametersAndReturnTypeSignatureComparer.Equals(candidate, implemented) Then
                            matchingStub = candidate
                            Exit For
                        End If
                    Next

                    If matchingStub Is Nothing Then
                        matchingStub = New SynthesizedInterfaceImplementationStubSymbol(method, implemented)
                        stubs.Add(matchingStub)

                        Dim f = New SyntheticBoundNodeFactory(matchingStub, matchingStub, method.Syntax, compilationState, New DiagnosticBag())

                        Dim methodToInvoke As MethodSymbol
                        If method.IsGenericMethod Then
                            methodToInvoke = method.Construct(matchingStub.TypeArguments)
                        Else
                            methodToInvoke = method
                        End If

                        Dim arguments = ArrayBuilder(Of BoundExpression).GetInstance(matchingStub.ParameterCount)

                        For Each param In matchingStub.Parameters
                            Dim parameterExpression = f.Parameter(param)

                            If Not param.IsByRef Then
                                parameterExpression = parameterExpression.MakeRValue()
                            End If

                            arguments.Add(parameterExpression)
                        Next

                        Dim invocation = f.Call(f.Me, methodToInvoke, arguments.ToImmutableAndFree())
                        Dim body As BoundBlock

                        If method.IsSub Then
                            body = f.Block(f.ExpressionStatement(invocation), f.Return())
                        Else
                            body = f.Block(f.Return(invocation))
                        End If

                        f.CloseMethod(body)
                        _moduleBeingBuiltOpt.AddSynthesizedDefinition(method.ContainingType, DirectCast(matchingStub, Microsoft.Cci.IMethodDefinition))
                    End If

                    matchingStub.AddImplementedMethod(implemented)
                End If
            Next

            If stubs IsNot Nothing Then
                For Each stub In stubs
                    stub.Seal()
                Next

                stubs.Free()
            End If
        End Sub

        Private Shared Function GetDesignerInitializeComponentMethod(sourceTypeSymbol As SourceMemberContainerTypeSymbol) As MethodSymbol

            If sourceTypeSymbol.TypeKind = TypeKind.Class AndAlso sourceTypeSymbol.GetAttributes().IndexOfAttribute(sourceTypeSymbol, AttributeDescription.DesignerGeneratedAttribute) > -1 Then
                For Each member As Symbol In sourceTypeSymbol.GetMembers("InitializeComponent")
                    If member.Kind = SymbolKind.Method Then
                        Dim method = DirectCast(member, MethodSymbol)

                        If method.IsSub AndAlso Not method.IsShared AndAlso Not method.IsGenericMethod AndAlso method.ParameterCount = 0 Then
                            Return method
                        End If
                    End If
                Next
            End If

            Return Nothing
        End Function

        Private Sub CompileSynthesizedMethods(privateImplClass As PrivateImplementationDetails)
            Debug.Assert(_moduleBeingBuiltOpt IsNot Nothing)

            For Each method As MethodSymbol In privateImplClass.GetMethods(Nothing)
                Dim diagnosticsThisMethod = DiagnosticBag.GetInstance()

                Dim boundBody = method.GetBoundMethodBody(diagnosticsThisMethod)

                Dim variableSlotAllocatorOpt = _moduleBeingBuiltOpt.TryCreateVariableSlotAllocator(method)
                Dim emittedBody = GenerateMethodBody(_moduleBeingBuiltOpt,
                                                     method,
                                                     boundBody,
                                                     variableSlotAllocatorOpt:=variableSlotAllocatorOpt,
                                                     debugDocumentProvider:=Nothing,
                                                     diagnostics:=diagnosticsThisMethod,
                                                     namespaceScopes:=Nothing)

                _diagnostics.AddRange(diagnosticsThisMethod)
                diagnosticsThisMethod.Free()

                ' error while generating IL
                If emittedBody Is Nothing Then
                    Exit For
                End If

                _moduleBeingBuiltOpt.SetMethodBody(method, emittedBody)
            Next
        End Sub

        Private Sub CompileSynthesizedMethods(additionalTypes As ImmutableArray(Of NamedTypeSymbol))
            Debug.Assert(_moduleBeingBuiltOpt IsNot Nothing)

            Dim compilationState As New TypeCompilationState(_compilation, _moduleBeingBuiltOpt, initializeComponentOpt:=Nothing)
            For Each additionalType In additionalTypes
                For Each method In additionalType.GetMethodsToEmit()
                    Dim diagnosticsThisMethod = DiagnosticBag.GetInstance()

                    Dim boundBody = method.GetBoundMethodBody(diagnosticsThisMethod)

                    Dim emittedBody As MethodBody = Nothing

                    If Not diagnosticsThisMethod.HasAnyErrors Then
                        Dim variableSlotAllocatorOpt = _moduleBeingBuiltOpt.TryCreateVariableSlotAllocator(method)
                        Dim rewrittenBody = Rewriter.LowerBodyOrInitializer(
                            method,
                            boundBody,
                            previousSubmissionFields:=Nothing,
                            compilationState:=compilationState,
                            diagnostics:=diagnosticsThisMethod,
                            variableSlotAllocatorOpt:=variableSlotAllocatorOpt,
                            isBodySynthesized:=True)

                        If Not diagnosticsThisMethod.HasAnyErrors Then
                            emittedBody = GenerateMethodBody(_moduleBeingBuiltOpt,
                                                             method,
                                                             rewrittenBody,
                                                             variableSlotAllocatorOpt:=variableSlotAllocatorOpt,
                                                             debugDocumentProvider:=Nothing,
                                                             diagnostics:=diagnosticsThisMethod,
                                                             namespaceScopes:=Nothing)
                        End If
                    End If

                    _diagnostics.AddRange(diagnosticsThisMethod)
                    diagnosticsThisMethod.Free()

                    ' error while generating IL
                    If emittedBody Is Nothing Then
                        Exit For
                    End If

                    _moduleBeingBuiltOpt.SetMethodBody(method, emittedBody)
                Next
            Next

            If Not _diagnostics.HasAnyErrors() Then
                CompileSynthesizedMethods(compilationState)
            End If

            compilationState.Free()
        End Sub

        Private Sub CompileSynthesizedMethods(compilationState As TypeCompilationState)
            Debug.Assert(_moduleBeingBuiltOpt IsNot Nothing)

            If Not compilationState.HasSynthesizedMethods Then
                Return
            End If

            For Each methodWithBody In compilationState.SynthesizedMethods
                Dim method = methodWithBody.Method

                Dim diagnosticsThisMethod As DiagnosticBag = DiagnosticBag.GetInstance()
                Debug.Assert(_moduleBeingBuiltOpt.TryCreateVariableSlotAllocator(method) Is Nothing)

                Dim emittedBody = GenerateMethodBody(_moduleBeingBuiltOpt,
                                                     method,
                                                     methodWithBody.Body,
                                                     variableSlotAllocatorOpt:=Nothing,
                                                     debugDocumentProvider:=_debugDocumentProvider,
                                                     diagnostics:=diagnosticsThisMethod,
                                                     namespaceScopes:=GetNamespaceScopes(methodWithBody.Method))

                _diagnostics.AddRange(diagnosticsThisMethod)
                diagnosticsThisMethod.Free()

                ' error while generating IL
                If emittedBody Is Nothing Then
                    Exit For
                End If

                _moduleBeingBuiltOpt.SetMethodBody(method, emittedBody)
            Next
        End Sub

        ''' <summary>
        ''' Detects cycles in constructor invocations based on the 'constructor-calls-constructor' 
        ''' map provided in 'constructorCallMap', reports errors if found.
        ''' 
        ''' NOTE: 'constructorCallMap' is being mutated by this method
        ''' </summary>
        Private Sub DetectAndReportCyclesInConstructorCalls(constructorCallMap As Dictionary(Of MethodSymbol, MethodSymbol), diagnostics As DiagnosticBag)

            Debug.Assert(constructorCallMap.Count > 0)

            Dim constructorsInPath As New Dictionary(Of MethodSymbol, Integer)
            Dim constructorsPath = ArrayBuilder(Of MethodSymbol).GetInstance()

            Dim currentMethod As MethodSymbol = constructorCallMap.Keys.First()

            ' Cycle constructor calls
            Do
                '  Where this constructor points to?
                Dim currentMethodPointTo As MethodSymbol = Nothing
                If Not constructorCallMap.TryGetValue(currentMethod, currentMethodPointTo) Then

                    ' We didn't find anything, which means we maybe already processed 'currentMethod'
                    ' or it does not reference another constructor of this type, or there is somethig wrong with it;

                    ' In any case we can restart iteration, none of the constructors in path are part of cycles

                Else
                    ' 'currentMethod' references another constructor; we may safely remove 'currentMethod' 
                    ' from 'constructorCallMap' because we won't need to process it again
                    constructorCallMap.Remove(currentMethod)

                    constructorsInPath.Add(currentMethod, constructorsPath.Count)
                    constructorsPath.Add(currentMethod)

                    Dim foundAt As Integer
                    If constructorsInPath.TryGetValue(currentMethodPointTo, foundAt) Then

                        ' We found a cycle which starts at 'foundAt' and goes to the end of constructorsPath
                        constructorsPath.Add(currentMethodPointTo)
                        ReportConstructorCycles(foundAt, constructorsPath.Count - 1, constructorsPath, diagnostics)

                        ' We can restart iteration, none of the constructors 
                        ' in path may be part of other cycles

                    Else
                        ' No cycles so far, just move to the next constructor
                        currentMethod = currentMethodPointTo
                        Continue Do
                    End If

                End If

                ' Restart iteration 
                constructorsInPath.Clear()
                constructorsPath.Clear()

                If constructorCallMap.Count = 0 Then
                    ' Nothing left
                    constructorsPath.Free()
                    Exit Sub
                End If

                currentMethod = constructorCallMap.Keys.First()
            Loop

            Throw ExceptionUtilities.Unreachable

        End Sub

        ''' <summary> All the constructors in the cycle will be reported </summary>
        Private Sub ReportConstructorCycles(startsAt As Integer, endsAt As Integer,
                                            path As ArrayBuilder(Of MethodSymbol),
                                            diagnostics As DiagnosticBag)

            ' Cycle is: constructorsCycle(startsAt) --> 
            '               constructorsCycle(startsAt + 1) --> 
            '                   ....
            '                       constructorsCycle(endsAt) = constructorsCycle(startsAt)
            '
            ' In case the constructor constructorsCycle(startsAt) calls itself, startsAt = endsAt + 1

            Debug.Assert(startsAt <= endsAt)
            Debug.Assert(path(startsAt).Equals(path(endsAt)))

            '  Generate cycle info
            Dim diagnosticInfos = ArrayBuilder(Of DiagnosticInfo).GetInstance()
            Dim referencingMethod As MethodSymbol = path(startsAt)
            For i = startsAt + 1 To endsAt
                Dim referencedMethod As MethodSymbol = path(i)
                diagnosticInfos.Add(ErrorFactory.ErrorInfo(ERRID.ERR_SubNewCycle2, referencingMethod, referencedMethod))
                referencingMethod = referencedMethod
            Next

            '  Report Errors for all constructors in the cycle
            For i = startsAt To endsAt - 1
                referencingMethod = path(i)

                '  Report an error
                diagnostics.Add(
                    New VBDiagnostic(ErrorFactory.ErrorInfo(ERRID.ERR_SubNewCycle1, referencingMethod,
                                                          New CompoundDiagnosticInfo(diagnosticInfos.ToArray())),
                                   referencingMethod.Locations(0)))

                '  Rotate 'diagnosticInfos' for the next constructor
                If diagnosticInfos.Count > 1 Then
                    Dim diagnostic = diagnosticInfos(0)
                    diagnosticInfos.RemoveAt(0)
                    diagnosticInfos.Add(diagnostic)
                End If
            Next

            diagnosticInfos.Free()
        End Sub

        Friend Shared Function CanBindMethod(method As MethodSymbol) As Boolean
            If method.IsExternalMethod OrElse method.IsMustOverride Then
                Return False
            End If

            ' Synthesized struct constructors are not emitted.
            If method.IsDefaultValueTypeConstructor() Then
                Return False
            End If

            If method.IsPartialWithoutImplementation Then
                ' Exclude partial methods without implementation
                Return False
            End If

            If Not method.IsImplicitlyDeclared Then
                ' Only compile the method if the method has a body.
                Dim sourceMethod = TryCast(method, SourceMethodSymbol)
                If sourceMethod Is Nothing OrElse sourceMethod.BlockSyntax Is Nothing Then
                    Return False
                End If
            End If

            Return True
        End Function

        ''' <summary>
        ''' Compiles the method.
        ''' </summary>
        ''' <param name="referencedConstructor">
        ''' If the method being compiled is a constructor, CompileMethod returns in this parameter 
        ''' the symbol of the constructor called from the one being compiled either explicitly or implicitly. 
        ''' For structure constructors calling parameterless constructor returns the synthesized constructor symbol.
        ''' </param>
        Private Sub CompileMethod(
            method As MethodSymbol,
            filter As Predicate(Of Symbol),
            compilationState As TypeCompilationState,
            processedInitializers As Binder.ProcessedFieldOrPropertyInitializers,
            containingTypeBinder As Binder,
            previousSubmissionFields As SynthesizedSubmissionFields,
            Optional ByRef referencedConstructor As MethodSymbol = Nothing
        )
            '' TODO: add filtering as follows
            'If filter IsNot Nothing AndAlso Not filter(method) Then
            '    Return
            'End If

            _cancellationToken.ThrowIfCancellationRequested()

            Dim sourceMethod = TryCast(method, SourceMethodSymbol)
            'get cached diagnostics if not building and we have 'em
            If Not DoEmitPhase AndAlso (sourceMethod IsNot Nothing) Then
                Dim cachedDiagnostics = sourceMethod.Diagnostics
                If Not cachedDiagnostics.IsDefault Then
                    Me._diagnostics.AddRange(cachedDiagnostics)
                    Return
                End If
            End If

            If Not CanBindMethod(method) Then
                If sourceMethod IsNot Nothing AndAlso sourceMethod.SetDiagnostics(ImmutableArray(Of Diagnostic).Empty) Then
                    sourceMethod.DeclaringCompilation.SymbolDeclaredEvent(method)
                End If

                Return
            End If

            ' In order to avoid generating code for methods with errors, we create a diagnostic bag just for this method.
            Dim diagsForCurrentMethod As DiagnosticBag = DiagnosticBag.GetInstance()

            Dim methodBinderOpt As Binder = Nothing
            Dim injectConstructorCall As Boolean
            Dim block = BindAndAnalyzeMethodBody(method, compilationState, diagsForCurrentMethod, containingTypeBinder, referencedConstructor, injectConstructorCall, methodBinderOpt)

            If method.MethodKind = MethodKind.Constructor OrElse method.MethodKind = MethodKind.SharedConstructor Then
                ' If this is a constructor and we do have initializers they need to be flow-analyzed for 
                ' warnings like 'Function '??' doesn't return a value on all code paths' or unreferenced variables. 

                ' Because if there are any initializers they will eventually get to one or more constructors
                ' it does not matter which constructor is being used as a method symbol for their analysis, 
                ' so we don't perform any analysis of which constructor symbol to pass to EnsureInitializersAnalyzed,
                ' but call this method for on all constructor symbols making sure instance/static initializers 
                ' are analyzed on the first instance/static constructor processed
                processedInitializers.EnsureInitializersAnalyzed(method, diagsForCurrentMethod)
            End If

            Dim hasErrors = _hasDeclarationErrors OrElse diagsForCurrentMethod.HasAnyErrors() OrElse processedInitializers.HasAnyErrors OrElse block.HasErrors
            SetGlobalErrorIfTrue(hasErrors)

            If sourceMethod IsNot Nothing AndAlso sourceMethod.SetDiagnostics(diagsForCurrentMethod.ToReadOnly()) Then
                Dim compilation = compilationState.Compilation
                If compilation.EventQueue IsNot Nothing Then
                    If block Is Nothing Then
                        compilation.SymbolDeclaredEvent(sourceMethod)
                    Else
                        'create a compilation event that caches the already-computed bound tree
                        Dim lazySemanticModel = New Lazy(Of SemanticModel)(
                            Function()
                                Dim syntax = block.Syntax
                                Dim semanticModel = CType(compilation.GetSemanticModel(syntax.SyntaxTree), SyntaxTreeSemanticModel)
                                Dim memberModel = CType(semanticModel.GetMemberSemanticModel(syntax), MethodBodySemanticModel)
                                If memberModel IsNot Nothing Then
                                    memberModel.CacheBoundNodes(block, syntax)
                                End If
                                Return semanticModel
                            End Function)
                        compilation.EventQueue.Enqueue(New SymbolDeclaredCompilationEvent(compilation, method, lazySemanticModel))
                    End If
                End If
            End If

            If Not DoEmitPhase AndAlso sourceMethod IsNot Nothing Then
                _diagnostics.AddRange(sourceMethod.Diagnostics)
                Return
            End If

            If DoEmitPhase AndAlso Not hasErrors Then
                LowerAndEmitMethod(method,
                                   block,
                                   If(methodBinderOpt, containingTypeBinder),
                                   compilationState,
                                   diagsForCurrentMethod,
                                   processedInitializers,
                                   previousSubmissionFields,
                                   If(injectConstructorCall, referencedConstructor, Nothing),
                                   GetNamespaceScopes(method))

                ' if method happen to handle events of a base WithEvents, ensure that we have an overriding WithEvents property
                Dim handledEvents = method.HandledEvents
                If _moduleBeingBuiltOpt IsNot Nothing AndAlso Not handledEvents.IsEmpty Then
                    CreateSyntheticWithEventOverridesIfNeeded(handledEvents,
                                                              compilationState,
                                                              containingTypeBinder,
                                                              diagsForCurrentMethod,
                                                              previousSubmissionFields)
                End If
            End If

            ' Add the generated diagnostics into the full diagnostic bag.
            _diagnostics.AddRange(diagsForCurrentMethod)
            diagsForCurrentMethod.Free()
        End Sub

        Private Function GetNamespaceScopes(method As MethodSymbol) As ImmutableArray(Of Cci.NamespaceScope)
            Debug.Assert(Me._generateDebugInfo = (_namespaceScopeBuilder IsNot Nothing))

            If _generateDebugInfo AndAlso method.GenerateDebugInfo Then
                Return _namespaceScopeBuilder.GetNamespaceScopes(method)
            End If

            Return Nothing
        End Function

        ''' <summary> 
        ''' If any of the "Handles" in the list have synthetic WithEvent override
        ''' as a container, then this method will (if not done already) inject 
        ''' property/accessors symbol into the emit module and assign bodies to the accessors.
        ''' </summary>
        Private Sub CreateSyntheticWithEventOverridesIfNeeded(handledEvents As ImmutableArray(Of HandledEvent),
                                      compilationState As TypeCompilationState,
                                      containingTypeBinder As Binder,
                                      diagsForCurrentMethod As DiagnosticBag,
                                      previousSubmissionFields As SynthesizedSubmissionFields)

            For Each handledEvent In handledEvents
                If handledEvent.HandlesKind = HandledEventKind.WithEvents Then
                    Dim prop = TryCast(handledEvent.hookupMethod.AssociatedSymbol, SynthesizedOverridingWithEventsProperty)
                    If prop IsNot Nothing Then
                        Dim accessor = prop.GetMethod
                        If Not compilationState.HasMethodWrapper(accessor) Then
                            Dim body = accessor.GetBoundMethodBody(diagsForCurrentMethod, containingTypeBinder)

                            ' no need to rewrite getter, they are pretty simple and 
                            ' are already in a lowered form.
                            compilationState.AddMethodWrapper(accessor, accessor, body)
                            _moduleBeingBuiltOpt.AddSynthesizedDefinition(accessor.ContainingType, accessor)

                            ' deal with setter too
                            accessor = prop.SetMethod
                            body = accessor.GetBoundMethodBody(diagsForCurrentMethod, containingTypeBinder)

                            ' setter needs to rewritten as it may require lambda conversions
                            body = Rewriter.LowerBodyOrInitializer(accessor,
                                                                   body,
                                                                   previousSubmissionFields,
                                                                   compilationState,
                                                                   diagsForCurrentMethod,
                                                                   variableSlotAllocatorOpt:=Nothing,
                                                                   isBodySynthesized:=True)

                            compilationState.AddMethodWrapper(accessor, accessor, body)
                            _moduleBeingBuiltOpt.AddSynthesizedDefinition(accessor.ContainingType, accessor)

                            ' add property too
                            _moduleBeingBuiltOpt.AddSynthesizedDefinition(prop.ContainingType, prop)
                        End If
                    End If
                End If
            Next
        End Sub

        ''' <summary> 
        ''' Assuming the statement is a constructor call wrapped in bound expression 
        ''' statement, get the method symbol being called 
        ''' </summary>
        Private Shared Function TryGetMethodCalledInBoundExpressionStatement(stmt As BoundExpressionStatement) As MethodSymbol

            '  No statement provided or has errors
            If stmt Is Nothing OrElse stmt.HasErrors Then
                Return Nothing
            End If

            '  Statement is not a call
            Dim expression As BoundExpression = stmt.Expression
            If expression.Kind <> BoundKind.Call Then
                Return Nothing
            End If

            Return DirectCast(expression, BoundCall).Method
        End Function

        Private Sub LowerAndEmitMethod(
            method As MethodSymbol,
            block As BoundBlock,
            binderOpt As Binder,
            compilationState As TypeCompilationState,
            diagsForCurrentMethod As DiagnosticBag,
            processedInitializers As Binder.ProcessedFieldOrPropertyInitializers,
            previousSubmissionFields As SynthesizedSubmissionFields,
            constructorToInject As MethodSymbol,
            namespaceScopes As ImmutableArray(Of Cci.NamespaceScope)
        )
            Dim constructorInitializerOpt = If(constructorToInject Is Nothing,
                                               Nothing,
                                               BindDefaultConstructorInitializer(method, constructorToInject, diagsForCurrentMethod, binderOpt))

            If diagsForCurrentMethod.HasAnyErrors Then
                Return
            End If

            If constructorInitializerOpt IsNot Nothing AndAlso constructorInitializerOpt.HasErrors Then
                Return
            End If

            Dim body As BoundBlock
            If method.MethodKind = MethodKind.Constructor OrElse method.MethodKind = MethodKind.SharedConstructor Then
                ' Turns field initializers into bound assignment statements and top-level script statements into bound statements in the beginning the body. 
                ' For submission constructor, the body only includes bound initializers and a return statement. The rest is filled in later.
                body = InitializerRewriter.BuildConstructorBody(
                    compilationState,
                    method,
                    If(method.IsSubmissionConstructor, Nothing, constructorInitializerOpt),
                    processedInitializers,
                    block)
            Else
                body = block
            End If

            Dim diagnostics As DiagnosticBag = diagsForCurrentMethod

            If method.IsImplicitlyDeclared AndAlso
               method.AssociatedSymbol IsNot Nothing AndAlso
               method.AssociatedSymbol.IsMyGroupCollectionProperty Then
                diagnostics = DiagnosticBag.GetInstance()
            End If

            Dim variableSlotAllocatorOpt = If(_moduleBeingBuiltOpt Is Nothing, Nothing, _moduleBeingBuiltOpt.TryCreateVariableSlotAllocator(method))
            body = Rewriter.LowerBodyOrInitializer(method,
                                                   body,
                                                   previousSubmissionFields,
                                                   compilationState,
                                                   diagnostics,
                                                   variableSlotAllocatorOpt,
                                                   isBodySynthesized:=False)

            ' The submission initializer has to be constructed after the body is rewritten (all previous submission references are visited):
            Dim submissionInitialization As ImmutableArray(Of BoundStatement)
            If method.IsSubmissionConstructor Then
                submissionInitialization = SynthesizedSubmissionConstructorSymbol.MakeSubmissionInitialization(block.Syntax, method, previousSubmissionFields, _compilation, diagnostics)
            Else
                submissionInitialization = Nothing
            End If

            Dim hasErrors = body.HasErrors OrElse diagsForCurrentMethod.HasAnyErrors OrElse (diagnostics IsNot diagsForCurrentMethod AndAlso diagnostics.HasAnyErrors)
            SetGlobalErrorIfTrue(hasErrors)

            ' Actual emitting is only done if we have a module in which to emit and no errors so far.
            If _moduleBeingBuiltOpt Is Nothing OrElse hasErrors Then
                If diagnostics IsNot diagsForCurrentMethod Then
                    DirectCast(method.AssociatedSymbol, SynthesizedMyGroupCollectionPropertySymbol).RelocateDiagnostics(diagnostics, diagsForCurrentMethod)
                    diagnostics.Free()
                End If

                Return
            End If

            ' now we have everything we need to build complete submission
            If method.IsSubmissionConstructor Then
                Debug.Assert(previousSubmissionFields IsNot Nothing)

                Dim boundStatements = ArrayBuilder(Of BoundStatement).GetInstance(1 + submissionInitialization.Length + 1)
                boundStatements.Add(constructorInitializerOpt)
                boundStatements.AddRange(submissionInitialization)
                boundStatements.Add(body)
                body = New BoundBlock(body.Syntax, Nothing, ImmutableArray(Of LocalSymbol).Empty, boundStatements.ToImmutableAndFree(), body.HasErrors).MakeCompilerGenerated()
            End If

            ' NOTE: additional check for statement.HasErrors is needed to identify parse errors which didn't get into diagsForCurrentMethod
            Dim methodBody As MethodBody = GenerateMethodBody(_moduleBeingBuiltOpt,
                                                              method,
                                                              body,
                                                              variableSlotAllocatorOpt,
                                                              _debugDocumentProvider,
                                                              diagnostics,
                                                              namespaceScopes)

            If diagnostics IsNot diagsForCurrentMethod Then
                DirectCast(method.AssociatedSymbol, SynthesizedMyGroupCollectionPropertySymbol).RelocateDiagnostics(diagnostics, diagsForCurrentMethod)
                diagnostics.Free()
            End If

            _moduleBeingBuiltOpt.SetMethodBody(If(method.PartialDefinitionPart, method), methodBody)
        End Sub

        Friend Shared Function GenerateMethodBody(moduleBuilder As PEModuleBuilder,
                                                  method As MethodSymbol,
                                                  block As BoundStatement,
                                                  variableSlotAllocatorOpt As VariableSlotAllocator,
                                                  debugDocumentProvider As DebugDocumentProvider,
                                                  diagnostics As DiagnosticBag,
                                                  namespaceScopes As ImmutableArray(Of Cci.NamespaceScope)) As MethodBody

            Dim compilation = moduleBuilder.Compilation
            Dim localSlotManager = New LocalSlotManager(variableSlotAllocatorOpt)
            Dim optimizations = compilation.Options.OptimizationLevel

            If method.IsEmbedded Then
                optimizations = OptimizationLevel.Release
            End If

            Dim builder As ILBuilder = New ILBuilder(moduleBuilder, localSlotManager, optimizations)
            Dim emittingPdbs = Not namespaceScopes.IsDefault

            Try
                Debug.Assert(Not diagnostics.HasAnyErrors)

                Dim asyncDebugInfo As Cci.AsyncMethodBodyDebugInfo = Nothing
                Dim codeGen = New CodeGen.CodeGenerator(method, block, builder, moduleBuilder, diagnostics, optimizations, emittingPdbs)

                ' We need to save additional debugging information for MoveNext of an async state machine.
                Dim stateMachineMethod = TryCast(method, SynthesizedStateMachineMethod)
                If stateMachineMethod IsNot Nothing AndAlso
                   stateMachineMethod.StateMachineType.KickoffMethod.IsAsync AndAlso
                   method.Name = WellKnownMemberNames.MoveNextMethodName Then

                    Dim asyncCatchHandlerOffset As Integer = -1
                    Dim asyncYieldPoints As ImmutableArray(Of Integer) = Nothing
                    Dim asyncResumePoints As ImmutableArray(Of Integer) = Nothing

                    codeGen.Generate(asyncCatchHandlerOffset, asyncYieldPoints, asyncResumePoints)

                    Dim kickoffMethod = stateMachineMethod.StateMachineType.KickoffMethod

                    ' In VB async method may be partial. Debug info needs to be associated with the emitted definition, 
                    ' but the kickoff method is the method implementation (the part with body).
                    asyncDebugInfo = New Cci.AsyncMethodBodyDebugInfo(If(kickoffMethod.PartialDefinitionPart, kickoffMethod), asyncCatchHandlerOffset, asyncYieldPoints, asyncResumePoints)
                Else
                    codeGen.Generate()
                End If

                If diagnostics.HasAnyErrors() Then
                    Return Nothing
                End If

                ' We will only save the IL builders when running tests.
                If moduleBuilder.SaveTestData Then
                    moduleBuilder.SetMethodTestData(method, builder.GetSnapshot())
                End If

                ' edgeInclusive must be true as that is what VB EE expects.

                '<PdbUtil.cpp>
                'HRESULT()
                'PdbUtil::IsScopeInOffset(
                '    _In_ ISymUnmanagedScope* pScope,
                '    _In_ DWORD offset,
                '    _Out_ bool* inOffset)
                '{
                '    VerifyInPtr(pScope);
                '    VerifyOutPtr(inOffset);

                '    HRESULT hr;
                '    ULONG32 start, end;

                '    IfFailGo( pScope->GetStartOffset(&start) );
                '    IfFailGo( pScope->GetEndOffset(&end) );
                '    *inOffset = (end >= offset) && (offset >= start);      <====  HERE
                'Error:
                '    return hr;
                '}

                Return New MethodBody(builder.RealizedIL,
                                      builder.MaxStack,
                                      If(method.PartialDefinitionPart, method),
                                      builder.LocalSlotManager.LocalsInOrder(),
                                      builder.RealizedSequencePoints,
                                      debugDocumentProvider,
                                      builder.RealizedExceptionHandlers,
                                      builder.GetAllScopes(edgeInclusive:=True),
                                      hasDynamicLocalVariables:=False,
                                      namespaceScopes:=namespaceScopes,
                                      namespaceScopeEncoding:=Cci.NamespaceScopeEncoding.Forwarding,
                                      iteratorClassName:=Nothing,
                                      iteratorScopes:=Nothing,
                                      asyncMethodDebugInfo:=asyncDebugInfo)
            Finally
                ' Free resources used by the basic blocks in the builder.
                builder.FreeBasicBlocks()
            End Try
        End Function

        Friend Shared Function BindAndAnalyzeMethodBody(method As MethodSymbol,
                                                       compilationState As TypeCompilationState,
                                                       diagnostics As DiagnosticBag,
                                                       containingTypeBinder As Binder,
                                                       ByRef referencedConstructor As MethodSymbol,
                                                       ByRef injectDefaultConstructorCall As Boolean,
                                                       ByRef methodBodyBinder As Binder) As BoundBlock

            referencedConstructor = Nothing
            injectDefaultConstructorCall = False
            methodBodyBinder = Nothing

            Dim body = method.GetBoundMethodBody(diagnostics, methodBodyBinder)
            Debug.Assert(body IsNot Nothing)

            Analyzer.AnalyzeMethodBody(method, body, diagnostics)
            DiagnosticsPass.IssueDiagnostics(body, diagnostics, method)

            Debug.Assert(method.IsFromCompilation(compilationState.Compilation))
            If Not method.IsShared AndAlso compilationState.InitializeComponentOpt IsNot Nothing AndAlso
               Not method.IsImplicitlyDeclared Then
                InitializeComponentCallTreeBuilder.CollectCallees(compilationState, method, body)
            End If

            '  Instance constructor should return the referenced constructor in 'referencedConstructor'
            If method.MethodKind = MethodKind.Constructor Then

                ' class constructors must inject call to the base
                injectDefaultConstructorCall = Not method.ContainingType.IsValueType

                ' Try find explicitly called constructor, it should be the first statement in the block
                If body IsNot Nothing AndAlso body.Statements.Length > 0 Then

                    Dim theFirstStatement As BoundStatement = body.Statements(0)

                    '  Must be BoundExpressionStatement/BoundCall
                    If theFirstStatement.HasErrors Then
                        injectDefaultConstructorCall = False

                    ElseIf theFirstStatement.Kind = BoundKind.ExpressionStatement Then

                        Dim referencedMethod As MethodSymbol = TryGetMethodCalledInBoundExpressionStatement(DirectCast(theFirstStatement, BoundExpressionStatement))
                        If referencedMethod IsNot Nothing AndAlso referencedMethod.MethodKind = MethodKind.Constructor Then
                            referencedConstructor = referencedMethod
                            injectDefaultConstructorCall = False
                        End If
                    End If

                End If

                ' If we didn't find explicitly referenced constructor, use implicitly generated call
                If injectDefaultConstructorCall Then

                    ' NOTE: We might generate an error in this call in case there is 
                    '       no parameterless constructor suitable for calling
                    referencedConstructor = FindConstructorToCallByDefault(method, diagnostics, If(methodBodyBinder, containingTypeBinder))
                End If

            End If

            Return body
        End Function

        Private Class InitializeComponentCallTreeBuilder
            Inherits BoundTreeWalker

            Private m_CalledMethods As HashSet(Of MethodSymbol)
            Private ReadOnly m_ContainingType As NamedTypeSymbol

            Private Sub New(containingType As NamedTypeSymbol)
                m_ContainingType = containingType
            End Sub

            Public Shared Sub CollectCallees(compilationState As TypeCompilationState, method As MethodSymbol, block As BoundBlock)
                Dim visitor As New InitializeComponentCallTreeBuilder(method.ContainingType)
                visitor.VisitBlock(block)

                If visitor.m_CalledMethods IsNot Nothing Then
                    compilationState.AddToInitializeComponentCallTree(method, visitor.m_CalledMethods.ToArray().AsImmutableOrNull())
                End If
            End Sub

            Public Overrides Function VisitCall(node As BoundCall) As BoundNode
                If node.ReceiverOpt IsNot Nothing AndAlso
                   (node.ReceiverOpt.Kind = BoundKind.MeReference OrElse node.ReceiverOpt.Kind = BoundKind.MyClassReference) AndAlso
                   Not node.Method.IsShared AndAlso node.Method.OriginalDefinition.ContainingType Is m_ContainingType Then

                    If m_CalledMethods Is Nothing Then
                        m_CalledMethods = New HashSet(Of MethodSymbol)(ReferenceEqualityComparer.Instance)
                    End If

                    m_CalledMethods.Add(node.Method.OriginalDefinition)
                End If

                Return MyBase.VisitCall(node)
            End Function
        End Class

        ' This method may force completion of attributes to calculate if a symbol is Obsolete. Since this method is only called during
        ' lowering of default constructors, this should not cause any cycles.
        Private Shared Function FindConstructorToCallByDefault(constructor As MethodSymbol, diagnostics As DiagnosticBag, Optional binderForAccessibilityCheckOpt As Binder = Nothing) As MethodSymbol
            Debug.Assert(constructor IsNot Nothing)
            Debug.Assert(constructor.MethodKind = MethodKind.Constructor)

            Dim containingType As NamedTypeSymbol = constructor.ContainingType
            Debug.Assert(Not containingType.IsValueType)

            If containingType.IsSubmissionClass Then
                ' TODO (tomat): report errors if not available
                Dim objectType = constructor.ContainingAssembly.GetSpecialType(SpecialType.System_Object)
                Return objectType.InstanceConstructors.Single()
            End If

            ' If the type is a structure, then invoke the default constructor on the current type.
            ' Otherwise, invoke the default constructor on the base type.
            Dim defaultConstructorType As NamedTypeSymbol = containingType.BaseTypeNoUseSiteDiagnostics
            If defaultConstructorType Is Nothing OrElse defaultConstructorType.IsErrorType Then
                ' possible if class System.Object in source doesn't have an explicit constructor
                Return Nothing
            End If

            ' If 'binderForAccessibilityCheckOpt' is not specified, containing type must be System.Object
            If binderForAccessibilityCheckOpt Is Nothing Then
                Debug.Assert(defaultConstructorType.IsObjectType)
            End If

            Dim candidate As MethodSymbol = Nothing
            Dim atLeastOneAccessibleCandidateFound As Boolean = False
            For Each m In defaultConstructorType.InstanceConstructors

                ' NOTE: Generic constructors are disallowed, but in case they 
                '       show up because of bad metadata, ignore them.
                If m.IsGenericMethod Then
                    Continue For
                End If

                If binderForAccessibilityCheckOpt IsNot Nothing Then
                    ' Use binder to check accessibility
                    If Not binderForAccessibilityCheckOpt.IsAccessible(m, useSiteDiagnostics:=Nothing, accessThroughType:=containingType) Then
                        Continue For
                    End If
                Else
                    ' If there is no binder, just check if the method is public
                    If m.DeclaredAccessibility <> Accessibility.Public Then
                        Continue For
                    End If

                    ' NOTE: if there is no binder, we will only be able to emit a call to parameterless 
                    '       constructor, but not for constructors with  optional parameters and/or ParamArray
                    If m.ParameterCount <> 0 Then
                        atLeastOneAccessibleCandidateFound = True  ' it is still accessible
                        Continue For
                    End If
                End If

                ' The constructor is accessible
                atLeastOneAccessibleCandidateFound = True

                ' Class constructors can be called with no parameters when the parameters are optional or paramarray.  However, for structures
                ' there cannot be any parameters.
                Dim canBeCalledWithNoParameters = If(containingType.IsReferenceType, m.CanBeCalledWithNoParameters(), m.ParameterCount = 0)

                If canBeCalledWithNoParameters Then

                    If candidate Is Nothing Then
                        candidate = m
                    Else
                        ' Too many candidates, use different errors for synthesized and regular constructors
                        If constructor.IsImplicitlyDeclared Then
                            ' Synthesized constructor
                            diagnostics.Add(New VBDiagnostic(
                                                ErrorFactory.ErrorInfo(ERRID.ERR_NoUniqueConstructorOnBase2, containingType, containingType.BaseTypeNoUseSiteDiagnostics),
                                                containingType.Locations(0)))
                        Else
                            ' Regular constructor
                            diagnostics.Add(New VBDiagnostic(
                                                ErrorFactory.ErrorInfo(ERRID.ERR_RequiredNewCallTooMany2, defaultConstructorType, containingType),
                                                constructor.Locations(0)))
                        End If

                        Return candidate
                    End If

                End If
            Next

            ' Generate an error 
            If candidate Is Nothing Then

                If atLeastOneAccessibleCandidateFound Then

                    ' Different errors for synthesized and regular constructors
                    If constructor.IsImplicitlyDeclared Then
                        ' Synthesized constructor
                        diagnostics.Add(New VBDiagnostic(
                                            ErrorFactory.ErrorInfo(ERRID.ERR_NoConstructorOnBase2, containingType, containingType.BaseTypeNoUseSiteDiagnostics),
                                            containingType.Locations(0)))
                    Else
                        ' Regular constructor
                        diagnostics.Add(New VBDiagnostic(
                                            ErrorFactory.ErrorInfo(ERRID.ERR_RequiredNewCall2, defaultConstructorType, containingType),
                                            constructor.Locations(0)))
                    End If

                Else
                    ' No accessible constructor
                    ' NOTE: Dev10 generates this error in 'Inherits' clause of the type, but it is not available 
                    '       in *all* cases, so changing the error location to containingType's location
                    diagnostics.Add(New VBDiagnostic(
                                        ErrorFactory.ErrorInfo(ERRID.ERR_NoAccessibleConstructorOnBase, containingType.BaseTypeNoUseSiteDiagnostics),
                                        containingType.Locations(0)))
                End If
            End If

            Debug.Assert(candidate <> constructor)

            ' If the candidate is Obsolete then report diagnostics.
            If candidate IsNot Nothing Then
                candidate.ForceCompleteObsoleteAttribute()

                If candidate.ObsoleteState = ThreeState.True Then
                    Dim data = candidate.ObsoleteAttributeData

                    ' If we have a synthesized constructor then give an error saying that there is no non-obsolete
                    ' base constructor. If we have a user-defined constructor then ask the user to explcitly call a
                    ' constructor so that they have a chance to call a non-obsolete base constructor.
                    If constructor.IsImplicitlyDeclared Then
                        ' Synthesized constructor.
                        If String.IsNullOrEmpty(data.Message) Then
                            diagnostics.Add(If(data.IsError, ERRID.ERR_NoNonObsoleteConstructorOnBase3, ERRID.WRN_NoNonObsoleteConstructorOnBase3),
                                            containingType.Locations(0),
                                            containingType,
                                            candidate,
                                            containingType.BaseTypeNoUseSiteDiagnostics)
                        Else
                            diagnostics.Add(If(data.IsError, ERRID.ERR_NoNonObsoleteConstructorOnBase4, ERRID.WRN_NoNonObsoleteConstructorOnBase4),
                                            containingType.Locations(0),
                                            containingType,
                                            candidate,
                                            containingType.BaseTypeNoUseSiteDiagnostics,
                                            data.Message)
                        End If
                    Else
                        ' Regular constructor.
                        If String.IsNullOrEmpty(data.Message) Then
                            diagnostics.Add(If(data.IsError, ERRID.ERR_RequiredNonObsoleteNewCall3, ERRID.WRN_RequiredNonObsoleteNewCall3),
                                            constructor.Locations(0),
                                            candidate,
                                            containingType.BaseTypeNoUseSiteDiagnostics,
                                            containingType)
                        Else
                            diagnostics.Add(If(data.IsError, ERRID.ERR_RequiredNonObsoleteNewCall4, ERRID.WRN_RequiredNonObsoleteNewCall4),
                                            constructor.Locations(0),
                                            candidate,
                                            containingType.BaseTypeNoUseSiteDiagnostics,
                                            containingType,
                                            data.Message)
                        End If
                    End If
                End If
            End If

            Return candidate
        End Function

        Private Shared Function BindDefaultConstructorInitializer(constructor As MethodSymbol,
                                                                  constructorToCall As MethodSymbol,
                                                                  diagnostics As DiagnosticBag,
                                                                  Optional binderOpt As Binder = Nothing) As BoundExpressionStatement

            Dim voidType As NamedTypeSymbol = constructor.ContainingAssembly.GetSpecialType(SpecialType.System_Void)
            ' NOTE: we can ignore use site errors in this place because they should have already be reported 
            '       either in real or synthesized constructor

            Dim syntaxNode As VisualBasicSyntaxNode = constructor.Syntax

            Dim thisRef As New BoundMeReference(syntaxNode, constructor.ContainingType)
            thisRef.SetWasCompilerGenerated()

            Dim baseInvocation As BoundExpression = Nothing
            If constructorToCall.ParameterCount = 0 Then

                ' If this is parameterless constructor, we can build a call directly
                baseInvocation = New BoundCall(syntaxNode, constructorToCall, Nothing, thisRef, ImmutableArray(Of BoundExpression).Empty, Nothing, voidType)

            Else

                ' Otherwise we should bind invocation exprerssion
                ' Binder must be passed in 'binderOpt'
                Debug.Assert(binderOpt IsNot Nothing)

                '  Build a method group
                Dim group As New BoundMethodGroup(constructor.Syntax,
                                                  typeArgumentsOpt:=Nothing,
                                                  methods:=ImmutableArray.Create(Of MethodSymbol)(constructorToCall),
                                                  resultKind:=LookupResultKind.Good,
                                                  receiverOpt:=thisRef,
                                                  qualificationKind:=QualificationKind.QualifiedViaValue)

                baseInvocation = binderOpt.BindInvocationExpression(constructor.Syntax,
                                                                    Nothing,
                                                                    TypeCharacter.None,
                                                                    group,
                                                                    ImmutableArray(Of BoundExpression).Empty,
                                                                    Nothing,
                                                                    diagnostics,
                                                                    callerInfoOpt:=Nothing,
                                                                    allowConstructorCall:=True)
            End If
            baseInvocation.SetWasCompilerGenerated()

            Dim statement As New BoundExpressionStatement(syntaxNode, baseInvocation)
            statement.SetWasCompilerGenerated()
            Return statement
        End Function

        Friend Shared Function BindDefaultConstructorInitializer(constructor As MethodSymbol, diagnostics As DiagnosticBag) As BoundExpressionStatement
            ' NOTE: this method is only called from outside

            ' NOTE: Because we don't pass a binder into this method, we assume that (a) containing type of 
            '       the constructor is a reference type inherited from System.Object (later is asserted 
            '       in 'FindConstructorToCallByDefault'), and (b) System.Object must have Public parameterless 
            '       constructor (asserted in 'BindDefaultConstructorInitializer')

            ' NOTE: We might generate an error in this call in case there is 
            '       no parameterless constructor suitable for calling
            Dim baseConstructor As MethodSymbol = FindConstructorToCallByDefault(constructor, diagnostics)
            If baseConstructor Is Nothing Then
                Return Nothing
            End If

            Return BindDefaultConstructorInitializer(constructor, baseConstructor, diagnostics)
        End Function

        Private Shared Function CreateDebugDocumentForFile(normalizedPath As String) As Cci.DebugSourceDocument
            Return New Cci.DebugSourceDocument(normalizedPath, Cci.DebugSourceDocument.CorSymLanguageTypeBasic)
        End Function

        Private Shared Function PassesFilter(filterOpt As Predicate(Of Symbol), symbol As Symbol) As Boolean
            Return filterOpt Is Nothing OrElse filterOpt(symbol)
        End Function
    End Class
End Namespace
