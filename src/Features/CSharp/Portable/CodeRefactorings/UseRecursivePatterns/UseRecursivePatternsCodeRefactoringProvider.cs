﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.CodeGeneration;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Simplification;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.CodeRefactorings.UseRecursivePatterns
{
    using static SyntaxKind;
    using static SyntaxFactory;

    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = PredefinedCodeRefactoringProviderNames.UseRecursivePatterns), Shared]
    internal sealed class UseRecursivePatternsCodeRefactoringProvider : CodeRefactoringProvider
    {
        private static readonly PatternSyntax s_trueConstantPattern = ConstantPattern(LiteralExpression(TrueLiteralExpression));
        private static readonly PatternSyntax s_falseConstantPattern = ConstantPattern(LiteralExpression(FalseLiteralExpression));

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public UseRecursivePatternsCodeRefactoringProvider()
        {
        }

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var (document, textSpan, cancellationToken) = context;
            if (document.Project.Solution.Workspace.Kind == WorkspaceKind.MiscellaneousFiles)
            {
                return;
            }

            if (textSpan.Length > 0)
            {
                return;
            }

            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var node = root.FindToken(textSpan.Start).Parent;
            var replacementFunc = GetReplacementFunc(node, model);
            if (replacementFunc is null)
                return;

            context.RegisterRefactoring(
                new MyCodeAction(
                    "Use recursive patterns",
                    _ => Task.FromResult(document.WithSyntaxRoot(replacementFunc(root)))));
        }

        private static Func<SyntaxNode, SyntaxNode>? GetReplacementFunc(SyntaxNode? node, SemanticModel model)
        {
            return node switch
            {
                BinaryExpressionSyntax(LogicalAndExpression) logicalAnd
                    => CombineLogicalAndOperands(logicalAnd, model),
                CasePatternSwitchLabelSyntax { WhenClause: { } whenClause } switchLabel
                    => CombineWhenClauseCondition(switchLabel.Pattern, whenClause.Condition, model),
                SwitchExpressionArmSyntax { WhenClause: { } whenClause } switchArm
                    => CombineWhenClauseCondition(switchArm.Pattern, whenClause.Condition, model),
                WhenClauseSyntax { Parent: CasePatternSwitchLabelSyntax switchLabel } whenClause
                    => CombineWhenClauseCondition(switchLabel.Pattern, whenClause.Condition, model),
                WhenClauseSyntax { Parent: SwitchExpressionArmSyntax switchArm } whenClause
                    => CombineWhenClauseCondition(switchArm.Pattern, whenClause.Condition, model),
                _ => null
            };
        }

        private static Func<SyntaxNode, SyntaxNode>? CombineLogicalAndOperands(BinaryExpressionSyntax logicalAnd, SemanticModel model)
        {
            if (TryDetermineReceiver(logicalAnd.Left, model) is not var (leftReceiver, leftTarget, leftFlipped) ||
                TryDetermineReceiver(logicalAnd.Right, model) is not var (rightReceiver, rightTarget, rightFlipped))
            {
                return null;
            }

            // If we have an is-expression on the left, first we check if there is a variable designation that's been used on the right-hand-side,
            // in which case, we'll convert and move the check inside the existing pattern, if possible.
            // For instance, `e is C c && c.p == 0` is converted to `e is C { p: 0 } c`
            if (leftTarget.Parent is IsPatternExpressionSyntax isPatternExpression &&
                TryFindVariableDesignation(isPatternExpression.Pattern, rightReceiver, model) is var (containingPattern, rightNamesOpt))
            {
                Debug.Assert(leftTarget == isPatternExpression.Pattern);
                Debug.Assert(leftReceiver == isPatternExpression.Expression);
                return root =>
                {
                    var rightPattern = CreatePattern(rightReceiver, rightTarget, rightFlipped);
                    var rewrittenPattern = RewriteContainingPattern(containingPattern, rightPattern, rightNamesOpt);
                    var replacement = isPatternExpression.ReplaceNode(containingPattern, rewrittenPattern);
                    return root.ReplaceNode(logicalAnd, AdjustBinaryExpressionOperands(logicalAnd, replacement));
                };
            }

            if (TryGetCommonReceiver(leftReceiver, rightReceiver, model) is not var (commonReceiverOpt, leftNames, rightNames))
                return null;

            return root =>
            {
                var leftSubpattern = CreateSubpattern(leftNames, CreatePattern(leftReceiver, leftTarget, leftFlipped));
                var rightSubpattern = CreateSubpattern(rightNames, CreatePattern(rightReceiver, rightTarget, rightFlipped));
                // If the common receiver is null, it's an implicit `this` reference in source.
                // For instance, `prop == 1 && field == 2` would be converted to `this is { prop: 1, field: 2 }`
                var replacement = IsPatternExpression(commonReceiverOpt ?? ThisExpression(), RecursivePattern(leftSubpattern, rightSubpattern));
                return root.ReplaceNode(logicalAnd, AdjustBinaryExpressionOperands(logicalAnd, replacement));
            };

            static SyntaxNode AdjustBinaryExpressionOperands(BinaryExpressionSyntax logicalAnd, ExpressionSyntax replacement)
            {
                // If there's a `&&` on the left we have picked the right-hand-side for the combination.
                // In which case, we should replace that instead of the whole `&&` operator in a chain.
                // For instance, `expr && a.b == 1 && a.c == 2` is converted to `expr && a is { b: 1, c: 2 }`
                if (logicalAnd.Left is BinaryExpressionSyntax(LogicalAndExpression) leftExpression)
                    replacement = leftExpression.WithRight(replacement);
                return replacement.WithAdditionalAnnotations(Formatter.Annotation);
            }
        }

        private static Func<SyntaxNode, SyntaxNode>? CombineWhenClauseCondition(
            PatternSyntax switchPattern, ExpressionSyntax condition, SemanticModel model)
        {
            if (TryDetermineReceiver(condition, model, inWhenClause: true) is not var (receiver, target, flipped) ||
                TryFindVariableDesignation(switchPattern, receiver, model) is not var (containingPattern, namesOpt))
            {
                return null;
            }

            return root =>
            {
                var editor = new SyntaxEditor(root, CSharpSyntaxGenerator.Instance);
                switch (receiver.Parent!.Parent)
                {
                    // This is the leftmost `&&` operand in a when-clause. Remove the left-hand-side which we've just morphed in the switch pattern.
                    // For instance, `case { p: var v } when v.q == 1 && expr:` would be converted to `case { p: { q: 1 } } v when expr:`
                    case BinaryExpressionSyntax(LogicalAndExpression) logicalAnd:
                        editor.ReplaceNode(logicalAnd, logicalAnd.Right);
                        break;
                    // If we reach here, there's no other expression left in the when-clause. Remove.
                    // For instance, `case { p: var v } when v.q == 1:` would be converted to `case { p: { q: 1 } v }:`
                    case WhenClauseSyntax whenClause:
                        editor.RemoveNode(whenClause, SyntaxRemoveOptions.AddElasticMarker);
                        break;
                    case var v:
                        throw ExceptionUtilities.UnexpectedValue(v);
                }

                var generatedPattern = CreatePattern(receiver, target, flipped);
                var rewrittenPattern = RewriteContainingPattern(containingPattern, generatedPattern, namesOpt);
                editor.ReplaceNode(containingPattern, rewrittenPattern);
                return editor.GetChangedRoot();
            };
        }

        private static PatternSyntax RewriteContainingPattern(
            PatternSyntax containingPattern,
            PatternSyntax generatedPattern,
            ImmutableArray<IdentifierNameSyntax> namesOpt)
        {
            // This is a variable designation match. We'll try to combine the generated
            // pattern from the right-hand-side into the containing pattern of this designation.
            PatternSyntax result;
            if (namesOpt.IsDefault)
            {
                // If there's no name, we will combine the pattern itself.
                result = (containingPattern, generatedPattern) switch
                {
                    // We know we have a var-pattern, declaration-pattern or a recursive-pattern on the left as the containing node of the variable designation.
                    // Depending on the generated pattern off of the expression on the right, we can give a better result by morphing it into the existing match.

                    // e.g. `e is var x && e is { p: 1 }` => `e is { p: 1 } x`
                    (VarPatternSyntax var, RecursivePatternSyntax { Designation: null } recursive)
                        => recursive.WithDesignation(var.Designation),

                    // e.g. `e is var x && e is C` => `e is C x`
                    (VarPatternSyntax var, TypePatternSyntax type)
                        => DeclarationPattern(type.Type, var.Designation),

                    // e.g. `is C x && x is { p: 1 }` => `is C { p: 1 } x`
                    (DeclarationPatternSyntax decl, RecursivePatternSyntax { Type: null, Designation: null } recursive)
                        => recursive.WithType(decl.Type).WithDesignation(decl.Designation),

                    // e.g. `is { p: 1 } x && x is C` => `is C { p: 1 } x`
                    (RecursivePatternSyntax { Type: null } recursive, TypePatternSyntax type)
                        => recursive.WithType(type.Type),

                    // In any other case, we fallback to an `and` pattern.
                    // UNDONE: This may result in a few unused variables which should be removed in later pass.
                    _ => BinaryPattern(AndPattern, containingPattern.Parenthesize(), generatedPattern.Parenthesize()),
                };
            }
            else
            {
                // Otherwise, we generate a subpattern per each name and rewrite as a recursive pattern.
                var subpattern = CreateSubpattern(namesOpt, generatedPattern);
                result = containingPattern switch
                {
                    // e.g. `case var x when x.p is 1` => `case { p: 1 } x`
                    VarPatternSyntax p => RecursivePattern(type: null, subpattern, p.Designation),

                    // e.g. `case Type x when x.p is 1` => `case Type { p: 1 } x`
                    DeclarationPatternSyntax p => RecursivePattern(p.Type, subpattern, p.Designation),

                    // e.g. `case { p: 1 } x when x.q is 2` => `case { p: 1, q: 2 } x`
                    RecursivePatternSyntax p => p.AddPropertyPatternClauseSubpatterns(subpattern),

                    // We've already checked that the designation is contained in any of the above pattern form.
                    var p => throw ExceptionUtilities.UnexpectedValue(p)
                };
            }

            // We must have preserved the existing variable designation.
            Debug.Assert(containingPattern switch
            {
                VarPatternSyntax p => p.Designation,
                DeclarationPatternSyntax p => p.Designation,
                RecursivePatternSyntax p => p.Designation,
                var p => throw ExceptionUtilities.UnexpectedValue(p)
            } is var d && result.DescendantNodes().Any(node => AreEquivalent(node, d)));

            return result
                .WithAdditionalAnnotations(Formatter.Annotation)
                .WithAdditionalAnnotations(Simplifier.Annotation);
        }

        private static PatternSyntax CreatePattern(ExpressionSyntax receiver, ExpressionOrPatternSyntax target, bool flipped)
        {
            return target switch
            {
                // A type or pattern come from an `is` expression on either side of `&&`
                PatternSyntax pattern => pattern,
                TypeSyntax type => TypePattern(type),
                // Otherwise this is a constant. Depending on the original receiver, we create an appropriate pattern.
                ExpressionSyntax constant => CreatePattern(receiver, constant, flipped),
                var v => throw ExceptionUtilities.UnexpectedValue(v),
            };
        }

        private static (PatternSyntax ContainingPattern, ImmutableArray<IdentifierNameSyntax> NamesOpt)? TryFindVariableDesignation(
            PatternSyntax leftPattern,
            ExpressionSyntax rightReceiver,
            SemanticModel model)
        {
            if (GetInnermostReceiver(rightReceiver, out var namesOpt, model) is not IdentifierNameSyntax identifierName)
                return null;

            var designation = leftPattern.DescendantNodes()
                .OfType<SingleVariableDesignationSyntax>()
                .Where(d => AreEquivalent(d.Identifier, identifierName.Identifier))
                .FirstOrDefault();

            // For simplicity, we only support replacement when the designation is contained in one of the following patterns.
            // This excludes a parenthesized variable designation, for example, which would require rewriting the whole thing.
            if (designation is not { Parent: PatternSyntax(SyntaxKind.VarPattern or SyntaxKind.DeclarationPattern or SyntaxKind.RecursivePattern) containingPattern })
                return null;

            return (containingPattern, namesOpt);
        }

        private static PatternSyntax CreatePattern(ExpressionSyntax receiver, ExpressionSyntax target, bool flipped)
        {
            return receiver.Parent switch
            {
                BinaryExpressionSyntax(EqualsExpression) => ConstantPattern(target),
                BinaryExpressionSyntax(NotEqualsExpression) => UnaryPattern(ConstantPattern(target)),
                BinaryExpressionSyntax(GreaterThanExpression or
                                       GreaterThanOrEqualExpression or
                                       LessThanOrEqualExpression or
                                       LessThanExpression) e
                    => RelationalPattern(flipped ? Flip(e.OperatorToken) : e.OperatorToken, target),
                var v => throw ExceptionUtilities.UnexpectedValue(v),
            };

            static SyntaxToken Flip(SyntaxToken token)
            {
                var kind = token.Kind() switch
                {
                    LessThanToken => GreaterThanToken,
                    LessThanEqualsToken => GreaterThanEqualsToken,
                    GreaterThanEqualsToken => LessThanEqualsToken,
                    GreaterThanToken => LessThanToken,
                    var v => throw ExceptionUtilities.UnexpectedValue(v)
                };
                return Token(token.LeadingTrivia, kind, token.TrailingTrivia);
            }
        }

        private static (ExpressionSyntax Receiver, ExpressionOrPatternSyntax Target, bool Flipped)? TryDetermineReceiver(
            ExpressionSyntax node,
            SemanticModel model,
            bool inWhenClause = false)
        {
            return node switch
            {
                // For comparison operators, after we have determined the
                // constant operand, we rewrite it as a constant or relational pattern.
                BinaryExpressionSyntax(EqualsExpression or
                                       NotEqualsExpression or
                                       GreaterThanExpression or
                                       GreaterThanOrEqualExpression or
                                       LessThanOrEqualExpression or
                                       LessThanExpression) expr
                    => TryDetermineConstant(expr, model),

                // If we found a `&&` here, there's two possibilities:
                //
                //  1) If we're in a when-clause, we look for the leftmost expression
                //     which we will try to combine with the switch arm/label pattern.
                //     For instance, we return `a` if we have `case <pat> when a && b && c`.

                //  2) Otherwise, we will return the operand that *appears* to be on the left in the source.
                //     For instance, we return `a` if we have `x && a && b` with the cursor on the second operator.
                //     Since `&&` is left-associative, it's guaranteed to be the expression that we want.
                //     For simplicity, we won't descend into any parenthesized expression here.
                //
                BinaryExpressionSyntax(LogicalAndExpression) expr
                    => TryDetermineReceiver(inWhenClause ? expr.Left : expr.Right, model, inWhenClause),

                // If we have an `is` operator, we'll try to combine the existing pattern/type with the other operand.
                BinaryExpressionSyntax(IsExpression) expr
                    => (expr.Left, expr.Right, false),
                IsPatternExpressionSyntax expr
                    => (expr.Expression, expr.Pattern, false),

                // We treat any other expression as if they were compared to true/false.
                // For instance, `a.b && !a.c` will be converted to `a is { b: true, c: false }`
                PrefixUnaryExpressionSyntax(LogicalNotExpression) expr
                    => (expr.Operand, s_falseConstantPattern, false),
                var expr => (expr, s_trueConstantPattern, false),
            };
        }

        private static (ExpressionSyntax Expression, ExpressionSyntax Constant, bool Flipped)? TryDetermineConstant(
            BinaryExpressionSyntax node,
            SemanticModel model)
        {
            return (node.Left, node.Right) switch
            {
                var (left, right) when model.GetConstantValue(left).HasValue => (right, left, true),
                var (left, right) when model.GetConstantValue(right).HasValue => (left, right, false),
                _ => null
            };
        }

        private static SubpatternSyntax CreateSubpattern(ImmutableArray<IdentifierNameSyntax> names, PatternSyntax pattern)
        {
            Debug.Assert(!names.IsDefaultOrEmpty);

            var subpattern = Subpattern(names[0], pattern);
            for (var i = 1; i < names.Length; i++)
                subpattern = Subpattern(names[i], RecursivePattern(subpattern));
            return subpattern;
        }

        public static SubpatternSyntax Subpattern(IdentifierNameSyntax name, PatternSyntax pattern)
            => SyntaxFactory.Subpattern(NameColon(name), pattern);

        public static RecursivePatternSyntax RecursivePattern(params SubpatternSyntax[] subpatterns)
            => SyntaxFactory.RecursivePattern(null, null, PropertyPatternClause(SeparatedList(subpatterns)), null);

        public static RecursivePatternSyntax RecursivePattern(SubpatternSyntax subpattern)
            => SyntaxFactory.RecursivePattern(null, null, PropertyPatternClause(SingletonSeparatedList(subpattern)), null);

        public static RecursivePatternSyntax RecursivePattern(TypeSyntax? type, SubpatternSyntax subpattern, VariableDesignationSyntax designation)
            => SyntaxFactory.RecursivePattern(type, null, PropertyPatternClause(SingletonSeparatedList(subpattern)), designation);

        /// <summary>
        /// Obtain the outermost common receiver between two expressions.
        /// </summary>
        private static (ExpressionSyntax? CommonReceiver, ImmutableArray<IdentifierNameSyntax> LeftNames, ImmutableArray<IdentifierNameSyntax> RightNames)? TryGetCommonReceiver(
            ExpressionSyntax left,
            ExpressionSyntax right,
            SemanticModel model)
        {
            using var _1 = ArrayBuilder<IdentifierNameSyntax>.GetInstance(out var leftNames);
            using var _2 = ArrayBuilder<IdentifierNameSyntax>.GetInstance(out var rightNames);

            if (!TryGetInnermostReceiver(left, leftNames, out var leftReceiver, model) ||
                !TryGetInnermostReceiver(right, rightNames, out var rightReceiver, model) ||
                !AreEquivalent(leftReceiver, rightReceiver)) // We must have a common starting point to proceed.
            {
                return null;
            }

            var commonReceiver = leftReceiver;
            ExpressionSyntax? lastName = null;

            // To reduce noise on superfluous subpatterns and avoid duplicates, skip any common name in the path.
            int leftIndex, rightIndex;
            for (leftIndex = leftNames.Count - 1, rightIndex = rightNames.Count - 1; leftIndex > 0 && rightIndex > 0; leftIndex--, rightIndex--)
            {
                var leftName = leftNames[leftIndex];
                var rightName = rightNames[rightIndex];
                if (!AreEquivalent(leftName, rightName))
                    break;
                lastName = leftName;
            }

            // .. and remove them from the set of names.
            leftNames.Clip(leftIndex + 1);
            rightNames.Clip(rightIndex + 1);

            if (lastName is not null)
            {
                // If there were some common names in the path, we rewrite the receiver to include those.
                // For instance, in `a.b.c && a.b.d`, we have `b` as the last common name in the path,
                // So we want `a.b` a the receiver so that we convert it to `a.b is { c: true, d: true }`.
                commonReceiver = GetReceiver(lastName, originalReceiver: left);
            }

            return (commonReceiver, leftNames.ToImmutable(), rightNames.ToImmutable());

            static ExpressionSyntax GetReceiver(ExpressionSyntax lastName, ExpressionSyntax originalReceiver)
            {
                return lastName.Parent switch
                {
                    // If the original receiver was not a conditional-access, this just return the parent node.
                    ExpressionSyntax expr when !originalReceiver.IsKind(SyntaxKind.ConditionalAccessExpression) => expr,
                    // Otherwise we rewrite the original receiver, with the expression on the left-hand-side of this name.
                    // For instance, if we had `a?.b.c.d && a.b.c.e` we rewrite it as `a?.b.c is { d: true, e: false }`
                    MemberAccessExpressionSyntax memberAccess => originalReceiver.ReplaceNode(memberAccess, memberAccess.Expression),
                    // Similarly, if we had `a?.b && a?.c` we rewrite it as `a is { b: true, c: true }`
                    MemberBindingExpressionSyntax { Parent: ConditionalAccessExpressionSyntax p } => p.Expression,
                    // There should be no other possibility as we have walked downwards already and verified each node in the path.
                    var v => throw ExceptionUtilities.UnexpectedValue(v),
                };
            }
        }

        private static ExpressionSyntax? GetInnermostReceiver(
            ExpressionSyntax node,
            out ImmutableArray<IdentifierNameSyntax> namesOpt,
            SemanticModel model)
        {
            using var _ = ArrayBuilder<IdentifierNameSyntax>.GetInstance(out var builder);
            TryGetInnermostReceiver(node, builder, out var receiver, model);
            namesOpt = builder.ToImmutableOrNull();
            return receiver;
        }

        private static bool TryGetInnermostReceiver(
            ExpressionSyntax node,
            ArrayBuilder<IdentifierNameSyntax> builder,
            out ExpressionSyntax? receiver,
            SemanticModel model)
        {
            receiver = GetInnermostReceiver(node);
            return builder.Any();

            ExpressionSyntax? GetInnermostReceiver(ExpressionSyntax node)
            {
                switch (node)
                {

                    case IdentifierNameSyntax name
                    when CanConvertToSubpattern(name):
                        builder.Add(name);
                        // This is a member reference with an implicit `this` receiver.
                        return null;
                    case MemberBindingExpressionSyntax { Name: IdentifierNameSyntax name }
                    when CanConvertToSubpattern(name):
                        builder.Add(name);
                        // We only reach here from a parent conditional-access.
                        // Returning null here means that all the names on the right are convertible to a property pattern.
                        return null;
                    case MemberAccessExpressionSyntax(SimpleMemberAccessExpression) { Name: IdentifierNameSyntax name } e
                    when CanConvertToSubpattern(name):
                        builder.Add(name);
                        // For a simple member access we simply record the name and descend into the expression on the left-hand-side.
                        return GetInnermostReceiver(e.Expression);
                    case ConditionalAccessExpressionSyntax e:
                        // For a conditional access, first we need to verify the right-hand-side is convertible to a property pattern.
                        var right = GetInnermostReceiver(e.WhenNotNull);
                        if (right is not null)
                        {
                            // If it has it's own receiver other than a member-binding expression, we return this node as the receiver.
                            // For instance, if we had `a?.M().b`, the name `b` is already captured, so we need to return `a?.M()` as the innermost receiver.
                            return e.WithWhenNotNull(right);
                        }

                        // Otherwise, descend into the the expression on the left-hand-side.
                        return GetInnermostReceiver(e.Expression);
                    default:
                        return node;
                }
            }

            bool CanConvertToSubpattern(IdentifierNameSyntax name)
            {
                return model.GetSymbolInfo(name).Symbol is
                {
                    IsStatic: false,
                    Kind: SymbolKind.Property or SymbolKind.Field,
                    ContainingType: not { SpecialType: SpecialType.System_Nullable_T }
                };
            }
        }

        private sealed class MyCodeAction : CodeActions.CodeAction.DocumentChangeAction
        {
            public MyCodeAction(string title, Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(title, createChangedDocument)
            {
            }
        }
    }
}
