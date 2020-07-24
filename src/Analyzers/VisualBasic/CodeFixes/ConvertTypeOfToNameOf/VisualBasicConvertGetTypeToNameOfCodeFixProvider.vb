﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Composition
Imports System.Diagnostics.CodeAnalysis
Imports Microsoft.CodeAnalysis.CodeFixes
Imports Microsoft.CodeAnalysis.ConvertTypeOfToNameOf
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.ConvertTypeOfToNameOf

    <ExportCodeFixProvider(LanguageNames.VisualBasic, Name:=PredefinedCodeFixProviderNames.ConvertTypeOfToNameOf), [Shared]>
    Friend Class VisualBasicConvertGetTypeToNameOfCodeFixProvider
        Inherits AbstractConvertTypeOfToNameOfCodeFixProvider

        <ImportingConstructor>
        <SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification:="Used in test code: https://github.com/dotnet/roslyn/issues/42814")>
        Public Sub New()
        End Sub

        Protected Overrides Function GetCodeFixTitle(visualbasic As String, csharp As String) As String
            Return visualbasic
        End Function

        Protected Overrides Function GetTypeExpression(node As SyntaxNode) As SyntaxNode
            Dim expression = DirectCast(node, MemberAccessExpressionSyntax).Expression
            Return DirectCast(expression, GetTypeExpressionSyntax).Type
        End Function
    End Class
End Namespace
