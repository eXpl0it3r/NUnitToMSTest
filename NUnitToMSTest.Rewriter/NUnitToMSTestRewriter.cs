using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NUnitToMSTest.Rewriter
{
    public class NUnitToMSTestRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel m_semanticModel;
        private readonly bool m_rewriteAsserts;
        private readonly QualifiedNameSyntax m_namespaceMsTest;
        private readonly QualifiedNameSyntax m_namespaceNUnit;

        private class PerMethodState
        {
            private readonly SemanticModel m_semanticModel;
            private Lazy<ImmutableArray<ILocalSymbol>> m_localSymbols;

            public PerMethodState(SemanticModel semanticModel)
            {
                m_semanticModel = semanticModel;
                m_localSymbols = new Lazy<ImmutableArray<ILocalSymbol>>();
            }

            public bool DataRowSeen { get; set; }
            public ExpressionSyntax Description { get; set; }
            public MethodDeclarationSyntax CurrentMethod { get; private set; }
            public ImmutableArray<ILocalSymbol> CurrentMethodLocals => m_localSymbols.Value;

            public void Reset(MethodDeclarationSyntax method)
            {
                DataRowSeen = false;
                Description = null;
                CurrentMethod = method;
                m_localSymbols = new Lazy<ImmutableArray<ILocalSymbol>>(GetLocalSymbols);
            }

            private ImmutableArray<ILocalSymbol> GetLocalSymbols()
            {
                return m_semanticModel.LookupSymbols(CurrentMethod.Body.SpanStart)
                    .OfType<ILocalSymbol>().ToImmutableArray();
            }
        }

        private readonly PerMethodState m_perMethodState;
        private readonly List<Diagnostic> m_diagnostics = new List<Diagnostic>();

        public bool Changed { get; private set; }

        public IEnumerable<Diagnostic> Diagnostics => m_diagnostics;

        public NUnitToMSTestRewriter(SemanticModel semanticModel, bool rewriteAsserts = false)
        {
            m_semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
            m_rewriteAsserts = rewriteAsserts;
            m_perMethodState = new PerMethodState(m_semanticModel);

            m_namespaceMsTest =
                (QualifiedNameSyntax)SyntaxFactory.ParseName("Microsoft.VisualStudio.TestTools.UnitTesting");
            m_namespaceNUnit = (QualifiedNameSyntax)SyntaxFactory.ParseName("NUnit.Framework");
        }

        public override SyntaxNode VisitUsingDirective(UsingDirectiveSyntax node)
        {
            var existing = m_semanticModel.GetSymbolInfo(node.Name);

            if (m_namespaceNUnit.ToFullString().Equals(existing.Symbol?.ToDisplayString()))
            {
                node = node.WithName(m_namespaceMsTest);
                Changed = true;
            }

            return node;
        }

        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // Intialize
            m_perMethodState.Reset(node);

            // Process nodes / children
            node = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node);

            // Post processing
            if (m_perMethodState.DataRowSeen)
            {
                node = node.AddAttribute(SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("DataTestMethod")));

                if (node.AttributeLists.SelectMany(al => al.Attributes).All(a => a.Name.ToString() != "CLSCompliant"))
                {
                    var clsCompliant = SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("CLSCompliant"));
                    clsCompliant = clsCompliant.WithArgumentList(SyntaxFactory.AttributeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.AttributeArgument(
                                    SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)))));
                    node = node.AddAttribute(clsCompliant);
                }

                Changed = true;
            }

            if (m_perMethodState.Description != null)
            {
                var arguments = new SeparatedSyntaxList<AttributeArgumentSyntax>();
                arguments = arguments.Add(SyntaxFactory.AttributeArgument(m_perMethodState.Description));
                var description = SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("Description"));
                description = description.WithArgumentList(SyntaxFactory.AttributeArgumentList(arguments));

                node = node.AddAttribute(description);
                Changed = true;
            }

            return node;
        }

        public override SyntaxNode VisitAttribute(AttributeSyntax node)
        {
            node = (AttributeSyntax)base.VisitAttribute(node);
            return HandleAttribute(node);
        }

        private SyntaxNode HandleAttribute(AttributeSyntax node)
        {
            var existing = m_semanticModel.GetSymbolInfo(node.Name);

            string existingTypeName = existing.Symbol?.ContainingType?.ToDisplayString();
            var location = node.GetLocation();
            var originalNode = node;
            try
            {
                switch (existingTypeName)
                {
                    case "NUnit.Framework.SetUpAttribute":
                        node = node.WithName(SyntaxFactory.IdentifierName("TestInitialize"));
                        break;
                    case "NUnit.Framework.TearDownAttribute":
                        node = node.WithName(SyntaxFactory.IdentifierName("TestCleanup"));
                        break;
                    case "NUnit.Framework.OneTimeSetUpAttribute":
                        WarnIfCurrentMethodNotStatic("ClassInitialize", location);
                        node = node.WithName(SyntaxFactory.IdentifierName("ClassInitialize"));
                        break;
                    case "NUnit.Framework.OneTimeTearDownAttribute":
                        WarnIfCurrentMethodNotStatic("ClassCleanup", location);
                        node = node.WithName(SyntaxFactory.IdentifierName("ClassCleanup"));
                        break;

                    case "NUnit.Framework.PropertyAttribute":
                        node = node.WithName(SyntaxFactory.IdentifierName("TestProperty"))
                            .ConvertArgumentsToString(m_diagnostics, location);
                        break;

                    case "NUnit.Framework.TestFixtureAttribute":
                        node = node.WithName(SyntaxFactory.IdentifierName("TestClass"))
                            .WithoutArgumentList(m_diagnostics, location);
                        Changed = true;
                        break;
                    case "NUnit.Framework.TestCaseAttribute":
                        node = node.WithName(SyntaxFactory.IdentifierName("DataRow"))
                            .RenameNameEquals("TestName", "DisplayName");
                        m_perMethodState.DataRowSeen = true;
                        Changed = true;
                        break;
                    case "NUnit.Framework.TestCaseSourceAttribute":
                        node = TransformTestCaseSourceAttribute(node);
                        Changed = true;
                        break;
                    case "NUnit.Framework.TestAttribute":
                        m_perMethodState.Description = node.GetNameEqualsExpression("Description");
                        node = node.WithName(SyntaxFactory.IdentifierName("TestMethod"))
                            .WithoutArgumentList(m_diagnostics, location, "Description");
                        Changed = true;
                        break;
                    case "NUnit.Framework.CategoryAttribute":
                        node = node.WithName(SyntaxFactory.IdentifierName("TestCategory"));
                        Changed = true;
                        break;
                    case "NUnit.Framework.ExplicitAttribute":
                        node = TransformExplicitAttribute(node);
                        Changed = true;
                        break;
                    case "NUnit.Framework.IgnoreAttribute":
                        node = node.WithName(SyntaxFactory.IdentifierName("Ignore"))
                            .WithoutNameEquals("Until", m_diagnostics, location);
                        Changed = true;
                        break;
                    case "NUnit.Framework.DescriptionAttribute"
                        // With MSTest DescriptionAttribute only supported on methods 
                        when node.GetParentKind() == SyntaxKind.MethodDeclaration:
                        node = node.WithName(SyntaxFactory.IdentifierName("Description"));
                        Changed = true;
                        break;
                    default:
                        {
                            if (existingTypeName != null && existingTypeName.StartsWith("NUnit."))
                            {
                                // Replace (potential) unqualified name with qualified name.
                                // Otherwise, an attribute whose unqualified name is accidentally the same
                                // as that of some other, unrelated, attribute could semantically change (since we
                                // replace the "using NUnit.Framework" with "using <MSTest>").
                                var fullQualifiedName = SyntaxFactory.ParseName(existingTypeName);
                                m_diagnostics.Add(Diagnostic.Create(DiagnosticsDescriptors.UnsupportedAttribute, location,
                                    node.ToFullString()));
                                node = node.WithName(fullQualifiedName);
                                Changed = true;
                            }

                            break;
                        }
                }

                return node;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to process '{originalNode}' [{location}]: {ex.Message}", ex);
            }
        }

        private void WarnIfCurrentMethodNotStatic(string attributeName, Location location)
        {
            if (m_perMethodState.CurrentMethod.Modifiers.All(m => m.Kind() != SyntaxKind.StaticKeyword))
            {
                m_diagnostics.Add(Diagnostic.Create(DiagnosticsDescriptors.MethodMustBeStaticForAttribute, location,
                    attributeName, m_perMethodState.CurrentMethod.Identifier));
            }
        }

        private AttributeSyntax TransformExplicitAttribute(AttributeSyntax node)
        {
            var location = node.GetLocation();
            var original = node.ToFullString();

            // MSTest V2 does not support "[Explicit]".
            // Convert "[Explicit]" to "[Ignore("EXPLICIT")]"
            // Convert "[Explicit("yadayada")]" to "[Ignore("EXPLICIT: yadayada")]"

            string text = "EXPLICIT";
            var description = node.GetPositionExpression(0);
            if (description != null)
            {
                text += ": " + description.GetFirstToken().ValueText;
            }

            var literalExpression = SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal("\"" + text + "\"", text));

            var arguments = new SeparatedSyntaxList<AttributeArgumentSyntax>();
            arguments = arguments.Add(SyntaxFactory.AttributeArgument(literalExpression));

            node = node.WithName(SyntaxFactory.IdentifierName("Ignore")).WithArgumentList(
                SyntaxFactory.AttributeArgumentList(arguments));

            m_diagnostics.Add(Diagnostic.Create(DiagnosticsDescriptors.TransformedUnsupported, location, original,
                node.ToFullString()));

            return node;
        }

        private static string GetMethodContainingType(MethodDeclarationSyntax node)
        {
            do
            {
                var parent = node.Parent;
                if (parent is ClassDeclarationSyntax clazz)
                {
                    return clazz.Identifier.ToString();
                }
                if (parent is StructDeclarationSyntax strukt)
                {
                    return strukt.Identifier.ToString();
                }
            }
            while (node.Parent != null);
            return null;
        }

        private AttributeSyntax TransformTestCaseSourceAttribute(AttributeSyntax node)
        {
            var location = node.GetLocation();

            // There are a number of possible overloads for the TestCaseDataAttribute,
            // for sanity, we currently support only two:
            //
            //  [TestCaseData(string sourceName)]
            //  [TestCaseData(Type sourceType, string sourceName)]

            bool supported = false;
            string targetName = null;
            string targetType = null;
            string explicitTargetType = null;
            if (node.ArgumentList != null)
            {
                int count = node.ArgumentList.Arguments.Count;
                if (count == 1)
                {
                    var arg0 = node.ArgumentList.Arguments[0];
                    var type0 = m_semanticModel.GetTypeInfo(arg0.Expression);
                    if (type0.ConvertedType?.SpecialType == SpecialType.System_String &&
                        (targetName = arg0.Expression.GetLiteralString()) != null)
                    {
                        supported = true;
                        targetType = GetMethodContainingType(m_perMethodState.CurrentMethod);
                    }
                }
                else if (count == 2)
                {
                    var arg0 = node.ArgumentList.Arguments[0];
                    var arg1 = node.ArgumentList.Arguments[1];
                    var type0 = m_semanticModel.GetTypeInfo(arg0.Expression);
                    var type1 = m_semanticModel.GetTypeInfo(arg1.Expression);

                    if (m_semanticModel.TypeSymbolMatchesType(type0.ConvertedType, typeof(Type)) &&
                        arg0.Expression is TypeOfExpressionSyntax typeOfExpression &&
                        type1.ConvertedType?.SpecialType == SpecialType.System_String &&
                        (targetName = arg1.Expression.GetLiteralString()) != null)
                    {
                        targetType = m_semanticModel.GetTypeInfo(typeOfExpression.Type).ConvertedType?.ToString();
                        explicitTargetType = targetType;
                        supported = targetType != null;
                    }
                }
            }

            if (!supported)
            {
                m_diagnostics.Add(Diagnostic.Create(DiagnosticsDescriptors.UnsupportedAttributeUsage, location, node.Name,
                    "Specific syntax not supported."));
                return node;
            }

            string sourceType;
            if (m_semanticModel.Compilation.FindSymbol<MethodDeclarationSyntax>(
                symbol => symbol is IMethodSymbol method && method.Name == targetName && method.ContainingType.Name == targetType) != null)
            {
                sourceType = "DynamicDataSourceType.Method";
            }
            else if (m_semanticModel.Compilation.FindSymbol<PropertyDeclarationSyntax>(
                symbol => symbol is IPropertySymbol method && method.Name == targetName && method.ContainingType.Name == targetType) != null)
            {
                sourceType = "DynamicDataSourceType.Property";
            }
            else
            {
                m_diagnostics.Add(Diagnostic.Create(DiagnosticsDescriptors.UnsupportedAttributeUsage, location, node.Name,
                    "Source name must be a method or property"));
                return node;
            }

            var argList = new SeparatedSyntaxList<AttributeArgumentSyntax>();
            if (explicitTargetType != null)
            {
                // [DynamicData(targetName, typeof(targetType), DynamicDataSourceType.Method)]
                argList = argList.Add(SyntaxFactory.AttributeArgument(
                    SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(targetName))));
                argList = argList.Add(SyntaxFactory.AttributeArgument(
                    SyntaxFactory.TypeOfExpression(SyntaxFactory.IdentifierName(explicitTargetType))));
            }
            else
            {
                // [DynamicData(targetName, DynamicDataSourceType.Method)]
                argList = argList.Add(SyntaxFactory.AttributeArgument(
                    SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(targetName))));
            }

            if (sourceType != "DynamicDataSourceType.Method")
            {
                argList = argList.Add(SyntaxFactory.AttributeArgument(SyntaxFactory.ParseName(sourceType)));
            }

            node = node.WithName(SyntaxFactory.IdentifierName("DynamicData"));
            node = node.WithArgumentList(SyntaxFactory.AttributeArgumentList(argList).NormalizeWhitespace());

            return node;
        }

        public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            base.VisitInvocationExpression(node);

            if (m_rewriteAsserts)
            {
                node = HandleInvocationExpression(node);
            }

            return node;
        }

        private InvocationExpressionSyntax HandleInvocationExpression(InvocationExpressionSyntax node)
        {
            var info = m_semanticModel.GetSymbolInfo(node);

            if ("NUnit.Framework.Assert".Equals(info.Symbol?.ContainingType.ToDisplayString()) &&
                node.Expression is MemberAccessExpressionSyntax ma)
            {
                if ("That".Equals(ma.Name?.ToString()) && node.ArgumentList?.Arguments.Count > 0)
                {
                    var firstArgument = node.ArgumentList.Arguments[0];

                    if (node.ArgumentList.Arguments.Count == 1)
                    {
                        if (m_semanticModel.HasBooleanResult(firstArgument.Expression))
                        {
                            // A simple ==> Assert.That(<boolean expression>); 
                            ma = ma.WithName(SyntaxFactory.IdentifierName("IsTrue"));
                            node = node.WithExpression(ma);
                        }
                    }
                    else
                    {
                        var secondArgument = node.ArgumentList.Arguments[1];
                        var remainingArguments = new SeparatedSyntaxList<ArgumentSyntax>();
                        remainingArguments = remainingArguments.AddRange(node.ArgumentList.Arguments.Skip(2));

                        var details = new ExceptionSyntaxDetails();
                        if (TryGetExceptionFromThrowsStaticHelper(secondArgument, details) ||
                            TryGetExceptionDetails(secondArgument, "TypeOf", details))
                        {
                            if (!details.Inconclusive)
                            {
                                node = MSTestSyntaxFactory.ThrowsExceptionSyntax(firstArgument.Expression,
                                        details, remainingArguments)
                                    .WithLeadingTrivia(node.GetClosestWhitespaceTrivia(true));
                            }
                        }
                        else if (TryGetExceptionDetails(secondArgument, "InstanceOf", details) &&
                                 !details.Inconclusive)
                        {
                            node = MSTestSyntaxFactory.ThrowsExceptionInstanceOfSyntax(firstArgument.Expression,
                                    details, remainingArguments)
                                .WithLeadingTrivia(node.GetClosestWhitespaceTrivia(true));
                        }
                        else if (m_semanticModel.HasBooleanResult(firstArgument.Expression))
                        {
                            // A simple ==> Assert.That(<boolean expression>); 
                            ma = ma.WithName(SyntaxFactory.IdentifierName("IsTrue"));
                            node = node.WithExpression(ma);
                        }
                    }
                }
                else if ("Null".Equals(ma.Name?.ToString()))
                {
                    ma = ma.WithName(SyntaxFactory.IdentifierName("IsNull"));
                    node = node.WithExpression(ma);
                }
                else if ("NotNull".Equals(ma.Name?.ToString()))
                {
                    ma = ma.WithName(SyntaxFactory.IdentifierName("IsNotNull"));
                    node = node.WithExpression(ma);
                }
            }

            return node;
        }

        private bool TryGetExceptionFromThrowsStaticHelper(SyntaxNode node, ExceptionSyntaxDetails details)
        {
            // Handles Assert.That(() => Dummy(), Throws.ArgumentNullException);
            //                                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^

            if (node == null)
                throw new ArgumentNullException(nameof(node));

            CollectMatchDetails(node, details, staticHelperMode: true);

            if (TryGetExceptionFromThrowsStaticHelperSingleNode(node, details))
                return true;

            foreach (var checkNode in node.ChildNodes())
            {
                if (TryGetExceptionFromThrowsStaticHelper(checkNode, details))
                    return true;
            }

            details.Reset();
            return false;
        }

        private bool TryGetExceptionFromThrowsStaticHelperSingleNode(SyntaxNode node, ExceptionSyntaxDetails details)
        {
            if (node.GetExpression() is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Expression.EqualsString("Throws") &&
                    !memberAccess.Name.TryGetGenericNameSyntax(out _))
                {
                    //
                    // Manually disambiguate the following cases:
                    //
                    //      Assert.That(Foo, Throws.Exception)
                    //      Assert.That(Foo, Throws.Exception.TypeOf<Something>());
                    //
                    // The first one is a candidate for "static helper" the second one is simply a more verbose
                    // version of the "Throws.TypeOf<>()". Both could have be written like this:
                    //
                    //      Assert.That(Foo, Throws.TypeOf<Exception>());
                    //      Assert.That(Foo, Throws.TypeOf<Something>());
                    //

                    string parentName = memberAccess.Parent.GetName()?.ToString();
                    if (parentName != null && (parentName.StartsWith("TypeOf<") || parentName.StartsWith("InstanceOf<")))
                    {
                        return false;
                    }

                    details.TypeName = memberAccess.Name?.ToString();
                    return true;
                }
            }

            return false;
        }

        private bool TryGetExceptionDetails(SyntaxNode node, string exceptionMethod, ExceptionSyntaxDetails details)
        {
            // Handles Assert.That(() => Dummy(), Throws.TypeOf<ArgumentNullException>());
            //                                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
            // Handles Assert.That(() => Dummy(), Throws.Exception.TypeOf<ArgumentNullException>());
            //                                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
            // Handles Assert.That(() => Dummy(), Throws.InstanceOf<ArgumentNullException>());
            //                                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
            // Handles Assert.That(() => Dummy(), Throws.Exception.InstanceOf<ArgumentNullException>());
            //                                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

            if (node == null)
                throw new ArgumentNullException(nameof(node));
            if (exceptionMethod == null)
                throw new ArgumentNullException(nameof(exceptionMethod));

            CollectMatchDetails(node, details);

            if (TryGetExceptionDetailsFromSingleNode(node, exceptionMethod, details))
                return true;

            foreach (var checkNode in node.ChildNodes())
            {
                if (TryGetExceptionDetails(checkNode, exceptionMethod, details))
                    return true;
            }

            details.Reset();
            return false;
        }

        private static bool TryGetExceptionDetailsFromSingleNode(SyntaxNode node, string exceptionMethod, ExceptionSyntaxDetails details)
        {
            if (node.GetExpression() is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Expression.EqualsString("Throws") ||
                    memberAccess.Expression.EqualsString("Throws.Exception"))
                {
                    if (memberAccess.Name.TryGetGenericNameSyntax(out var genericName) &&
                        exceptionMethod.Equals(genericName.Identifier.ToString()) &&
                        genericName.NumberOfArguments() == 1)
                    {
                        details.TypeName = genericName.TypeArgumentList.Arguments[0].ToString();
                        return true;
                    }
                }
            }

            details.TypeName = null;
            return false;
        }

        private static void CollectMatchDetails(SyntaxNode node, ExceptionSyntaxDetails details, bool staticHelperMode = false)
        {
            if (node.GetExpression() is MemberAccessExpressionSyntax memberAccess)
            {
                string memberName = memberAccess.Name?.ToString();
                switch (memberName)
                {
                    case "Contains":
                        details.MatchType = MatchType.Contains;
                        details.MatchTypeArguments = memberAccess.GetParentInvocationArguments(details, 1);
                        break;
                    case "EqualTo":
                        details.MatchType = MatchType.EqualTo;
                        details.MatchTypeArguments = memberAccess.GetParentInvocationArguments(details, 1);
                        break;
                    case "StartsWith":
                    case "StartWith":
                        details.MatchType = MatchType.StartsWith;
                        details.MatchTypeArguments = memberAccess.GetParentInvocationArguments(details, 1);
                        break;
                    case "EndsWith":
                    case "EndWith":
                        details.MatchType = MatchType.EndsWith;
                        details.MatchTypeArguments = memberAccess.GetParentInvocationArguments(details, 1);
                        break;
                    case "Matches":
                    case "Match":
                        details.MatchType = MatchType.Matches;
                        details.MatchTypeArguments = memberAccess.GetParentInvocationArguments(details, 1);
                        break;

                    case "Message":
                        if (details.MatchType != MatchType.None)
                        {
                            details.MatchTarget = memberName;
                        }
                        else
                        {
                            details.SetInconclusive(memberName);
                        }

                        break;

                    case "Property":
                        if (details.MatchType != MatchType.None)
                        {
                            details.MatchTarget = memberName;
                            details.MatchTargetArguments = memberAccess.TransformParentInvocationArguments(details, 1,
                                (arg, i) =>
                                {
                                    // We need to turn 'Property("MemberName")' into '<object>.Membername'.
                                    // Thus we cannot handle something like 'Property(Func())', because that
                                    // would require either executing the code to get the actual string value,
                                    // or use reflection when writing the exception.
                                    string str = arg.Expression?.GetLiteralString();
                                    if (str != null)
                                        return SyntaxFactory.Argument(SyntaxFactory.IdentifierName(str));
                                    return null;
                                });
                        }
                        else
                        {
                            details.SetInconclusive(memberName);
                        }

                        break;

                    case "With":
                        if (details.MatchTarget == null ||
                            details.MatchType == MatchType.None)
                        {
                            details.SetInconclusive(memberName);
                        }

                        break;

                    default:
                        if (staticHelperMode)
                        {
                            if (memberName != null && !(memberName.EndsWith("Exception")))
                            {
                                details.SetInconclusive(memberName);
                            }
                        }
                        else
                        {
                            if (memberName != null && !(
                                    memberName.Equals("Throws") ||
                                    memberName.StartsWith("TypeOf<") ||
                                    memberName.StartsWith("InstanceOf<") ||
                                    memberName.Equals("Exception")))
                            {
                                details.SetInconclusive(memberName);
                            }
                        }

                        break;
                }
            }
        }
    }
}