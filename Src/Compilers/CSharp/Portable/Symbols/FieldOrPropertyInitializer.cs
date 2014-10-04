﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Represents a field initializer, a property initializer, or a global statement in script code.
    /// </summary>
    internal struct FieldOrPropertyInitializer
    {
        /// <summary>
        /// The field being initialized (possibly a backing field of a property), or null if this is a global statement.
        /// </summary>
        internal readonly FieldSymbol Field;

        /// <summary>
        /// A reference to <see cref="EqualsValueClauseSyntax"/> or <see cref="GlobalStatementSyntax"/>.
        /// </summary>
        internal readonly SyntaxReference Syntax;

        public FieldOrPropertyInitializer(FieldSymbol field, SyntaxReference syntax)
        {
            Debug.Assert(((object)field != null) || (syntax != null));
            Field = field;
            Syntax = syntax;
        }
    }
}
