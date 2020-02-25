﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Quantum.QsCompiler.DataTypes;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Microsoft.Quantum.QsCompiler.Transformations.Core;


namespace Microsoft.Quantum.QsCompiler.Transformations.ClassicallyControlledTransformation
{
    using ExpressionKind = QsExpressionKind<TypedExpression, Identifier, ResolvedType>;
    using ResolvedTypeKind = QsTypeKind<ResolvedType, UserDefinedType, QsTypeParameter, CallableInformation>;
    using TypeArgsResolution = ImmutableArray<Tuple<QsQualifiedName, NonNullable<string>, ResolvedType>>;

    // This transformation works in two passes.
    // 1st Pass: Hoist the contents of conditional statements into separate operations, where possible.
    // 2nd Pass: On the way down the tree, reshape conditional statements to replace Elif's and
    // top level OR and AND conditions with equivalent nested if-else statements. One the way back up
    // the tree, convert conditional statements into interface calls, where possible.
    // This relies on anything having type parameters must be a global callable.
    public static class ClassicallyControlledTransformation
    {
        public static QsCompilation Apply(QsCompilation compilation)
        {
            compilation = HoistTransformation.Apply(compilation);

            return ConvertConditions.Apply(compilation);
        }

        private class ConvertConditions : QsSyntaxTreeTransformation<ConvertConditions.TransformationState>
        {
            public static QsCompilation Apply(QsCompilation compilation)
            {
                var filter = new ConvertConditions(compilation);

                return new QsCompilation(compilation.Namespaces.Select(ns => filter.Namespaces.Transform(ns)).ToImmutableArray(), compilation.EntryPoints);
            }

            public class TransformationState
            {
                public readonly QsCompilation Compilation;

                public TransformationState(QsCompilation compilation)
                {
                    Compilation = compilation;
                }
            }

            private ConvertConditions(QsCompilation compilation) : base(new TransformationState(compilation))
            {
                this.Namespaces = new NamespaceTransformation(this);
                this.Statements = new StatementTransformation(this);
            }

            private class NamespaceTransformation : NamespaceTransformation<TransformationState>
            {
                public NamespaceTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                public override QsCallable onFunction(QsCallable c) => c; // Prevent anything in functions from being considered
            }

            private class StatementTransformation : StatementTransformation<TransformationState>
            {
                public StatementTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                /// <summary>
                /// Checks if the scope is valid for conversion to an operation call from the conditional control API.
                /// It is valid if there is exactly one statement in it and that statement is a call like expression statement.
                /// If valid, returns true with the identifier of the call like expression and the arguments of the
                /// call like expression, otherwise returns false with nulls.
                /// </summary>
                private (bool, TypedExpression, TypedExpression) IsValidScope(QsScope scope)
                {
                    // if the scope has exactly one statement in it and that statement is a call like expression statement
                    if (scope != null
                        && scope.Statements.Length == 1
                        && scope.Statements[0].Statement is QsStatementKind.QsExpressionStatement expr
                        && expr.Item.ResolvedType.Resolution.IsUnitType
                        && expr.Item.Expression is ExpressionKind.CallLikeExpression call
                        && !TypedExpression.IsPartialApplication(expr.Item.Expression)
                        && call.Item1.Expression is ExpressionKind.Identifier)
                    {
                        // We are dissolving the application of arguments here, so the call's type argument
                        // resolutions have to be moved to the 'identifier' sub expression.

                        var callTypeArguments = expr.Item.TypeArguments;
                        var idTypeArguments = call.Item1.TypeArguments;
                        var combinedTypeArguments = Utilities.GetCombinedType(callTypeArguments, idTypeArguments);

                        // This relies on anything having type parameters must be a global callable.
                        var newCallIdentifier = call.Item1;
                        if (combinedTypeArguments.Any()
                            && newCallIdentifier.Expression is ExpressionKind.Identifier id
                            && id.Item1 is Identifier.GlobalCallable global)
                        {
                            var globalCallable = Transformation.InternalState.Compilation.Namespaces
                                .Where(ns => ns.Name.Equals(global.Item.Namespace))
                                .Callables()
                                .FirstOrDefault(c => c.FullName.Name.Equals(global.Item.Name));

                            QsCompilerError.Verify(globalCallable != null, $"Could not find the global reference {global.Item.Namespace.Value + "." + global.Item.Name.Value}");

                            var callableTypeParameters = globalCallable.Signature.TypeParameters
                                .Select(x => x as QsLocalSymbol.ValidName);

                            QsCompilerError.Verify(callableTypeParameters.All(x => x != null), $"Invalid type parameter names.");

                            newCallIdentifier = new TypedExpression(
                                ExpressionKind.NewIdentifier(
                                    id.Item1,
                                    QsNullable<ImmutableArray<ResolvedType>>.NewValue(
                                        callableTypeParameters
                                        .Select(x => combinedTypeArguments.First(y => y.Item2.Equals(x.Item)).Item3).ToImmutableArray())),
                                combinedTypeArguments,
                                call.Item1.ResolvedType,
                                call.Item1.InferredInformation,
                                call.Item1.Range);
                        }

                        return (true, newCallIdentifier, call.Item2);
                    }

                    return (false, null, null);
                }

                #region Condition Converting Logic

                /// <summary>
                /// Creates an operation call from the conditional control API, given information
                /// about which operation to call and with what arguments.
                /// </summary>
                private TypedExpression CreateControlCall(BuiltIn opInfo, IEnumerable<OpProperty> properties, TypedExpression args, IEnumerable<ResolvedType> typeArgs)
                {
                    // Build the surrounding control call
                    var identifier = Utilities.CreateIdentifierExpression(
                        Identifier.NewGlobalCallable(new QsQualifiedName(opInfo.Namespace, opInfo.Name)),
                        typeArgs
                            .Zip(opInfo.TypeParameters, (type, param) => Tuple.Create(new QsQualifiedName(opInfo.Namespace, opInfo.Name), param, type))
                            .ToImmutableArray(),
                        Utilities.GetOperatorResolvedType(properties, args.ResolvedType));

                    // Creates type resolutions for the call expression
                    var opTypeArgResolutions = typeArgs
                        .SelectMany(x =>
                            x.Resolution is ResolvedTypeKind.TupleType tup
                            ? tup.Item
                            : ImmutableArray.Create(x))
                        .Where(x => x.Resolution.IsTypeParameter)
                        .Select(x => (x.Resolution as ResolvedTypeKind.TypeParameter).Item)
                        .GroupBy(x => (x.Origin, x.TypeName))
                        .Select(group =>
                        {
                            var typeParam = group.First();
                            return Tuple.Create(typeParam.Origin, typeParam.TypeName, ResolvedType.New(ResolvedTypeKind.NewTypeParameter(typeParam)));
                        })
                        .ToImmutableArray();

                    return Utilities.CreateCallLike(identifier, args, opTypeArgResolutions);
                }

                /// <summary>
                /// Creates an operation call from the conditional control API for non-literal Result comparisons.
                /// </summary>
                private TypedExpression CreateApplyConditionallyExpression(TypedExpression conditionExpr1, TypedExpression conditionExpr2, QsScope conditionScope, QsScope defaultScope)
                {
                    var (isConditionValid, conditionId, conditionArgs) = IsValidScope(conditionScope);
                    var (isDefaultValid, defaultId, defaultArgs) = IsValidScope(defaultScope);

                    if (!isConditionValid)
                    {
                        return null; // ToDo: Diagnostic message - condition block not valid
                    }

                    if (!isDefaultValid && defaultScope != null)
                    {
                        return null; // ToDo: Diagnostic message - default block exists, but is not valid
                    }

                    if (defaultScope == null)
                    {
                        (defaultId, defaultArgs) = Utilities.GetNoOp();
                    }

                    // Get characteristic properties from global id
                    var props = ImmutableHashSet<OpProperty>.Empty;
                    if (conditionId.ResolvedType.Resolution is ResolvedTypeKind.Operation op)
                    {
                        props = op.Item2.Characteristics.GetProperties();
                        if (defaultId != null && defaultId.ResolvedType.Resolution is ResolvedTypeKind.Operation defaultOp)
                        {
                            props = props.Intersect(defaultOp.Item2.Characteristics.GetProperties());
                        }
                    }

                    BuiltIn controlOpInfo;
                    (bool adj, bool ctl) = (props.Contains(OpProperty.Adjointable), props.Contains(OpProperty.Controllable));
                    if (adj && ctl)
                    {
                        controlOpInfo = BuiltIn.ApplyConditionallyCA;
                    }
                    else if (adj)
                    {
                        controlOpInfo = BuiltIn.ApplyConditionallyA;
                    }
                    else if (ctl)
                    {
                        controlOpInfo = BuiltIn.ApplyConditionallyC;
                    }
                    else
                    {
                        controlOpInfo = BuiltIn.ApplyConditionally;
                    }

                    var equality = Utilities.CreateValueTupleExpression(conditionId, conditionArgs);
                    var inequality = Utilities.CreateValueTupleExpression(defaultId, defaultArgs);
                    var controlArgs = Utilities.CreateValueTupleExpression(Utilities.CreateValueArray(conditionExpr1), Utilities.CreateValueArray(conditionExpr2), equality, inequality);
                    var targetArgsTypes = ImmutableArray.Create(conditionArgs.ResolvedType, defaultArgs.ResolvedType);
                    
                    return CreateControlCall(controlOpInfo, props, controlArgs, targetArgsTypes);
                }

                /// <summary>
                /// Creates an operation call from the conditional control API for Result literal comparisons.
                /// </summary>
                private TypedExpression CreateApplyIfExpression(QsResult result, TypedExpression conditionExpression, QsScope conditionScope, QsScope defaultScope)
                {
                    var (isConditionValid, conditionId, conditionArgs) = IsValidScope(conditionScope);
                    var (isDefaultValid, defaultId, defaultArgs) = IsValidScope(defaultScope);

                    BuiltIn controlOpInfo;
                    TypedExpression controlArgs;
                    ImmutableArray<ResolvedType> targetArgsTypes;

                    var props = ImmutableHashSet<OpProperty>.Empty;

                    if (isConditionValid)
                    {
                        // Get characteristic properties from global id
                        if (conditionId.ResolvedType.Resolution is ResolvedTypeKind.Operation op)
                        {
                            props = op.Item2.Characteristics.GetProperties();
                            if (defaultId != null && defaultId.ResolvedType.Resolution is ResolvedTypeKind.Operation defaultOp)
                            {
                                props = props.Intersect(defaultOp.Item2.Characteristics.GetProperties());
                            }
                        }

                        (bool adj, bool ctl) = (props.Contains(OpProperty.Adjointable), props.Contains(OpProperty.Controllable));

                        if (isDefaultValid)
                        {
                            if (adj && ctl)
                            {
                                controlOpInfo = BuiltIn.ApplyIfElseRCA;
                            }
                            else if (adj)
                            {
                                controlOpInfo = BuiltIn.ApplyIfElseRA;
                            }
                            else if (ctl)
                            {
                                controlOpInfo = BuiltIn.ApplyIfElseRC;
                            }
                            else
                            {
                                controlOpInfo = BuiltIn.ApplyIfElseR;
                            }

                            (TypedExpression, ImmutableArray<ResolvedType>) GetArgs(TypedExpression zeroId, TypedExpression zeroArgs, TypedExpression oneId, TypedExpression oneArgs) =>
                            (
                                Utilities.CreateValueTupleExpression(
                                    conditionExpression,
                                    Utilities.CreateValueTupleExpression(zeroId, zeroArgs),
                                    Utilities.CreateValueTupleExpression(oneId, oneArgs)),

                                ImmutableArray.Create(zeroArgs.ResolvedType, oneArgs.ResolvedType)
                            );

                            (controlArgs, targetArgsTypes) = (result == QsResult.Zero)
                                ? GetArgs(conditionId, conditionArgs, defaultId, defaultArgs)
                                : GetArgs(defaultId, defaultArgs, conditionId, conditionArgs);
                        }
                        else if (defaultScope == null)
                        {
                            if (adj && ctl)
                            {
                                controlOpInfo = (result == QsResult.Zero)
                                ? BuiltIn.ApplyIfZeroCA
                                : BuiltIn.ApplyIfOneCA;
                            }
                            else if (adj)
                            {
                                controlOpInfo = (result == QsResult.Zero)
                                ? BuiltIn.ApplyIfZeroA
                                : BuiltIn.ApplyIfOneA;
                            }
                            else if (ctl)
                            {
                                controlOpInfo = (result == QsResult.Zero)
                                ? BuiltIn.ApplyIfZeroC
                                : BuiltIn.ApplyIfOneC;
                            }
                            else
                            {
                                controlOpInfo = (result == QsResult.Zero)
                                ? BuiltIn.ApplyIfZero
                                : BuiltIn.ApplyIfOne;
                            }

                            controlArgs = Utilities.CreateValueTupleExpression(
                                conditionExpression,
                                Utilities.CreateValueTupleExpression(conditionId, conditionArgs));

                            targetArgsTypes = ImmutableArray.Create(conditionArgs.ResolvedType);
                        }
                        else
                        {
                            return null; // ToDo: Diagnostic message - default block exists, but is not valid
                        }

                    }
                    else
                    {
                        return null; // ToDo: Diagnostic message - condition block not valid
                    }

                    return CreateControlCall(controlOpInfo, props, controlArgs, targetArgsTypes);
                }

                /// <summary>
                /// Takes an expression that is the call to a conditional control API operation and the original statement,
                /// and creates a statement from the given expression.
                /// </summary>
                private QsStatement CreateControlStatement(QsStatement statement, TypedExpression callExpression)
                {
                    if (callExpression != null)
                    {
                        return new QsStatement(
                            QsStatementKind.NewQsExpressionStatement(callExpression),
                            statement.SymbolDeclarations,
                            QsNullable<QsLocation>.Null,
                            statement.Comments);
                    }
                    else
                    {
                        // ToDo: add diagnostic message here
                        return statement; // If the blocks can't be converted, return the original
                    }
                }

                /// <summary>
                /// Converts a conditional statement to an operation call from the conditional control API.
                /// </summary>
                private QsStatement ConvertConditionalToControlCall(QsStatement statement)
                {
                    var (isCondition, condition, conditionScope, defaultScope) = IsConditionWithSingleBlock(statement);

                    if (isCondition)
                    {
                        var (isCompareLiteral, literal, nonLiteral) = IsConditionedOnResultLiteralExpression(condition);
                        if (isCompareLiteral)
                        {
                            return CreateControlStatement(statement, CreateApplyIfExpression(literal, nonLiteral, conditionScope, defaultScope));
                        }
                        else
                        {
                            var (isCompareNonLiteral, conditionExpr1, conditionExpr2) = IsConditionedOnResultEqualityExpression(condition);
                            if (isCompareNonLiteral)
                            {
                                return CreateControlStatement(statement, CreateApplyConditionallyExpression(conditionExpr1, conditionExpr2, conditionScope, defaultScope));
                            }
                            else
                            {
                                // ToDo: Diagnostic message
                                return statement; // The condition does not fit a supported format.
                            }
                        }
                    }
                    else
                    {
                        // ToDo: Diagnostic message
                        return statement; // The reshaping of the conditional did not succeed.
                    }
                }

                #endregion

                #region Condition Checking Logic

                /// <summary>
                /// Checks if the statement is a condition statement that only has one conditional block in it (default blocks are optional).
                /// If it is, returns true along with the condition, the body of the conditional block, and, optionally, the body of the
                /// default block, otherwise returns false with nulls. If there is no default block, the last value of the return tuple will be null.
                /// </summary>
                private (bool, TypedExpression, QsScope, QsScope) IsConditionWithSingleBlock(QsStatement statement)
                {
                    if (statement.Statement is QsStatementKind.QsConditionalStatement condition && condition.Item.ConditionalBlocks.Length == 1)
                    {
                        return (true, condition.Item.ConditionalBlocks[0].Item1, condition.Item.ConditionalBlocks[0].Item2.Body, condition.Item.Default.ValueOr(null)?.Body);
                    }

                    return (false, null, null, null);
                }

                /// <summary>
                /// Checks if the expression is an equality comparison where one side is a Result literal.
                /// If it is, returns true along with the Result literal and the other expression in the
                /// equality, otherwise returns false with nulls.
                /// </summary>
                private (bool, QsResult, TypedExpression) IsConditionedOnResultLiteralExpression(TypedExpression expression)
                {
                    if (expression.Expression is ExpressionKind.EQ eq)
                    {
                        if (eq.Item1.Expression is ExpressionKind.ResultLiteral exp1)
                        {
                            return (true, exp1.Item, eq.Item2);
                        }
                        else if (eq.Item2.Expression is ExpressionKind.ResultLiteral exp2)
                        {
                            return (true, exp2.Item, eq.Item1);
                        }
                    }

                    return (false, null, null);
                }

                /// <summary>
                /// Checks if the expression is an equality comparison between two Result-typed expressions.
                /// If it is, returns true along with the two expressions, otherwise returns false with nulls.
                /// </summary>
                private (bool, TypedExpression, TypedExpression) IsConditionedOnResultEqualityExpression(TypedExpression expression)
                {
                    if (expression.Expression is ExpressionKind.EQ eq
                        && eq.Item1.ResolvedType.Resolution == ResolvedTypeKind.Result
                        && eq.Item2.ResolvedType.Resolution == ResolvedTypeKind.Result)
                    {
                        return (true, eq.Item1, eq.Item2);
                    }

                    return (false, null, null);
                }

                #endregion

                #region Condition Reshaping Logic

                /// <summary>
                /// Converts if-elif-else structures to nested if-else structures.
                /// </summary>
                private (bool, QsConditionalStatement) ProcessElif(QsConditionalStatement conditionStatment)
                {
                    if (conditionStatment.ConditionalBlocks.Length < 2) return (false, conditionStatment);

                    var subCondition = new QsConditionalStatement(conditionStatment.ConditionalBlocks.RemoveAt(0), conditionStatment.Default);
                    var secondConditionBlock = conditionStatment.ConditionalBlocks[1].Item2;

                    var subIfStatment = new QsStatement
                    (
                        QsStatementKind.NewQsConditionalStatement(subCondition),
                        LocalDeclarations.Empty,
                        secondConditionBlock.Location,
                        secondConditionBlock.Comments
                    );

                    var newDefault = QsNullable<QsPositionedBlock>.NewValue(new QsPositionedBlock(
                        new QsScope(ImmutableArray.Create(subIfStatment), secondConditionBlock.Body.KnownSymbols),
                        secondConditionBlock.Location,
                        QsComments.Empty));

                    return (true, new QsConditionalStatement(ImmutableArray.Create(conditionStatment.ConditionalBlocks[0]), newDefault));
                }

                /// <summary>
                /// Converts conditional statements whose top-most condition is an OR.
                /// Creates a nested structure without the top-most OR.
                /// </summary>
                private (bool, QsConditionalStatement) ProcessOR(QsConditionalStatement conditionStatment)
                {
                    // This method expects elif blocks to have been abstracted out
                    if (conditionStatment.ConditionalBlocks.Length != 1) return (false, conditionStatment);

                    var (condition, block) = conditionStatment.ConditionalBlocks[0];

                    if (condition.Expression is ExpressionKind.OR orCondition)
                    {
                        var subCondition = new QsConditionalStatement(ImmutableArray.Create(Tuple.Create(orCondition.Item2, block)), conditionStatment.Default);
                        var subIfStatment = new QsStatement
                        (
                            QsStatementKind.NewQsConditionalStatement(subCondition),
                            LocalDeclarations.Empty,
                            block.Location,
                            QsComments.Empty
                        );
                        var newDefault = QsNullable<QsPositionedBlock>.NewValue(new QsPositionedBlock(
                            new QsScope(ImmutableArray.Create(subIfStatment), block.Body.KnownSymbols),
                            block.Location,
                            QsComments.Empty));

                        return (true, new QsConditionalStatement(ImmutableArray.Create(Tuple.Create(orCondition.Item1, block)), newDefault));
                    }
                    else
                    {
                        return (false, conditionStatment);
                    }
                }

                /// <summary>
                /// Converts conditional statements whose top-most condition is an AND.
                /// Creates a nested structure without the top-most AND.
                /// </summary>
                private (bool, QsConditionalStatement) ProcessAND(QsConditionalStatement conditionStatment)
                {
                    // This method expects elif blocks to have been abstracted out
                    if (conditionStatment.ConditionalBlocks.Length != 1) return (false, conditionStatment);

                    var (condition, block) = conditionStatment.ConditionalBlocks[0];

                    if (condition.Expression is ExpressionKind.AND andCondition)
                    {
                        var subCondition = new QsConditionalStatement(ImmutableArray.Create(Tuple.Create(andCondition.Item2, block)), conditionStatment.Default);
                        var subIfStatment = new QsStatement
                        (
                            QsStatementKind.NewQsConditionalStatement(subCondition),
                            LocalDeclarations.Empty,
                            block.Location,
                            QsComments.Empty
                        );
                        var newBlock = new QsPositionedBlock(
                            new QsScope(ImmutableArray.Create(subIfStatment), block.Body.KnownSymbols),
                            block.Location,
                            QsComments.Empty);

                        return (true, new QsConditionalStatement(ImmutableArray.Create(Tuple.Create(andCondition.Item1, newBlock)), conditionStatment.Default));
                    }
                    else
                    {
                        return (false, conditionStatment);
                    }
                }

                /// <summary>
                /// Converts conditional statements to nested structures so they do not
                /// have elif blocks or top-most OR or AND conditions.
                /// </summary>
                private QsStatement ReshapeConditional(QsStatement statement)
                {
                    if (statement.Statement is QsStatementKind.QsConditionalStatement condition)
                    {
                        var stm = condition.Item;
                        (_, stm) = ProcessElif(stm);
                        bool wasOrProcessed, wasAndProcessed;
                        do
                        {
                            (wasOrProcessed, stm) = ProcessOR(stm);
                            (wasAndProcessed, stm) = ProcessAND(stm);
                        } while (wasOrProcessed || wasAndProcessed);

                        return new QsStatement
                        (
                            QsStatementKind.NewQsConditionalStatement(stm),
                            statement.SymbolDeclarations,
                            statement.Location,
                            statement.Comments
                        );
                    }
                    return statement;
                }

                #endregion

                public override QsScope Transform(QsScope scope)
                {
                    var parentSymbols = this.onLocalDeclarations(scope.KnownSymbols);
                    var statements = new List<QsStatement>();

                    foreach (var statement in scope.Statements)
                    {
                        if (statement.Statement is QsStatementKind.QsConditionalStatement)
                        {
                            var stm = ReshapeConditional(statement);
                            stm = this.onStatement(stm);
                            stm = ConvertConditionalToControlCall(stm);

                            statements.Add(stm);
                        }
                        else
                        {
                            statements.Add(this.onStatement(statement));
                        }
                    }

                    return new QsScope(statements.ToImmutableArray(), parentSymbols);
                }
            }
        }
    }

    // Transformation handling the first pass task of hoisting of the contents of conditional statements.
    // If blocks are first validated to see if they can safely be hoisted into their own operation.
    // Validation requirements are that there are no return statements and that there are no set statements
    // on mutables declared outside the block. Setting mutables declared inside the block is valid.
    // If the block is valid, and there is more than one statement in the block, a new operation with the
    // block's contents is generated, having all the same type parameters as the calling context
    // and all known variables at the start of the block become parameters to the new operation.
    // The contents of the conditional block are then replaced with a call to the new operation with all
    // the type parameters and known variables being forwarded to the new operation as arguments.
    public static class HoistTransformation
    {
        public static QsCompilation Apply(QsCompilation compilation) => HoistContents.Apply(compilation);

        private class HoistContents : QsSyntaxTreeTransformation<HoistContents.TransformationState>
        {
            public static QsCompilation Apply(QsCompilation compilation)
            {
                var filter = new HoistContents();

                return new QsCompilation(compilation.Namespaces.Select(ns => filter.Namespaces.Transform(ns)).ToImmutableArray(), compilation.EntryPoints);
            }

            public class CallableDetails
            {
                public QsCallable Callable;
                public QsSpecialization Adjoint;
                public QsSpecialization Controlled;
                public QsSpecialization ControlledAdjoint;
                public QsNullable<ImmutableArray<ResolvedType>> TypeParamTypes;

                public CallableDetails(QsCallable callable)
                {
                    Callable = callable;
                    Adjoint = callable.Specializations.FirstOrDefault(spec => spec.Kind == QsSpecializationKind.QsAdjoint);
                    Controlled = callable.Specializations.FirstOrDefault(spec => spec.Kind == QsSpecializationKind.QsControlled);
                    ControlledAdjoint = callable.Specializations.FirstOrDefault(spec => spec.Kind == QsSpecializationKind.QsControlledAdjoint);
                    TypeParamTypes = callable.Signature.TypeParameters.Any(param => param.IsValidName)
                    ? QsNullable<ImmutableArray<ResolvedType>>.NewValue(callable.Signature.TypeParameters
                        .Where(param => param.IsValidName)
                        .Select(param =>
                            ResolvedType.New(ResolvedTypeKind.NewTypeParameter(new QsTypeParameter(
                                callable.FullName,
                                ((QsLocalSymbol.ValidName)param).Item,
                                QsNullable<Tuple<QsPositionInfo, QsPositionInfo>>.Null
                        ))))
                        .ToImmutableArray())
                    : QsNullable<ImmutableArray<ResolvedType>>.Null;
                }
            }

            public class TransformationState
            {
                public bool IsValidScope = true;
                public List<QsCallable> ControlOperations = null;
                public ImmutableArray<LocalVariableDeclaration<NonNullable<string>>> CurrentHoistParams =
                    ImmutableArray<LocalVariableDeclaration<NonNullable<string>>>.Empty;
                public bool ContainsHoistParamRef = false;

                public CallableDetails CurrentCallable = null;
                public bool InBody = false;
                public bool InAdjoint = false;
                public bool InControlled = false;
                public bool InWithinBlock = false;

                private (ResolvedSignature, IEnumerable<QsSpecialization>) MakeSpecializations(
                    QsQualifiedName callableName, ResolvedType argsType, SpecializationImplementation bodyImplementation)
                {
                    QsSpecialization MakeSpec(QsSpecializationKind kind, ResolvedSignature signature, SpecializationImplementation impl) =>
                        new QsSpecialization(
                            kind,
                            callableName,
                            ImmutableArray<QsDeclarationAttribute>.Empty,
                            CurrentCallable.Callable.SourceFile,
                            QsNullable<QsLocation>.Null,
                            QsNullable<ImmutableArray<ResolvedType>>.Null,
                            signature,
                            impl,
                            ImmutableArray<string>.Empty,
                            QsComments.Empty);

                    var adj = CurrentCallable.Adjoint;
                    var ctl = CurrentCallable.Controlled;
                    var ctlAdj = CurrentCallable.ControlledAdjoint;

                    bool addAdjoint = false;
                    bool addControlled = false;

                    if (InWithinBlock)
                    {
                        addAdjoint = true;
                        addControlled = false;
                    }
                    else if (InBody)
                    {
                        if (adj != null && adj.Implementation is SpecializationImplementation.Generated adjGen) addAdjoint = adjGen.Item.IsInvert;
                        if (ctl != null && ctl.Implementation is SpecializationImplementation.Generated ctlGen) addControlled = ctlGen.Item.IsDistribute;
                        if (ctlAdj != null && ctlAdj.Implementation is SpecializationImplementation.Generated ctlAdjGen)
                        {
                            addAdjoint = addAdjoint || ctlAdjGen.Item.IsInvert && ctl.Implementation.IsGenerated;
                            addControlled = addControlled || ctlAdjGen.Item.IsDistribute && adj.Implementation.IsGenerated;
                        }
                    }
                    else if (ctlAdj != null && ctlAdj.Implementation is SpecializationImplementation.Generated gen)
                    {
                        addControlled = InAdjoint && gen.Item.IsDistribute;
                        addAdjoint = InControlled && gen.Item.IsInvert;
                    }

                    var props = new List<OpProperty>();
                    if (addAdjoint) props.Add(OpProperty.Adjointable);
                    if (addControlled) props.Add(OpProperty.Controllable);
                    var newSig = new ResolvedSignature(
                        CurrentCallable.Callable.Signature.TypeParameters,
                        argsType,
                        ResolvedType.New(ResolvedTypeKind.UnitType),
                        new CallableInformation(ResolvedCharacteristics.FromProperties(props), InferredCallableInformation.NoInformation));

                    var controlledSig = new ResolvedSignature(
                        newSig.TypeParameters,
                        ResolvedType.New(ResolvedTypeKind.NewTupleType(ImmutableArray.Create(
                            ResolvedType.New(ResolvedTypeKind.NewArrayType(ResolvedType.New(ResolvedTypeKind.Qubit))),
                            newSig.ArgumentType))),
                        newSig.ReturnType,
                        newSig.Information);

                    var specializations = new List<QsSpecialization>() { MakeSpec(QsSpecializationKind.QsBody, newSig, bodyImplementation) };

                    if (addAdjoint)
                    {
                        specializations.Add(MakeSpec(
                            QsSpecializationKind.QsAdjoint,
                            newSig,
                            SpecializationImplementation.NewGenerated(QsGeneratorDirective.Invert)));
                    }

                    if (addControlled)
                    {
                        specializations.Add(MakeSpec(
                            QsSpecializationKind.QsControlled,
                            controlledSig,
                            SpecializationImplementation.NewGenerated(QsGeneratorDirective.Distribute)));
                    }

                    if (addAdjoint && addControlled)
                    {
                        specializations.Add(MakeSpec(
                            QsSpecializationKind.QsControlledAdjoint,
                            controlledSig,
                            SpecializationImplementation.NewGenerated(QsGeneratorDirective.Distribute)));
                    }

                    return (newSig, specializations);
                }

                public (QsCallable, ResolvedType) GenerateOperation(QsScope contents)
                {
                    var newName = Utilities.AddGuid(CurrentCallable.Callable.FullName);

                    var knownVariables = contents.KnownSymbols.IsEmpty
                        ? ImmutableArray<LocalVariableDeclaration<NonNullable<string>>>.Empty
                        : contents.KnownSymbols.Variables;

                    var parameters = QsTuple<LocalVariableDeclaration<QsLocalSymbol>>.NewQsTuple(knownVariables
                        .Select(var => QsTuple<LocalVariableDeclaration<QsLocalSymbol>>.NewQsTupleItem(new LocalVariableDeclaration<QsLocalSymbol>(
                            QsLocalSymbol.NewValidName(var.VariableName),
                            var.Type,
                            var.InferredInformation,
                            var.Position,
                            var.Range)))
                        .ToImmutableArray());

                    var paramTypes = ResolvedType.New(ResolvedTypeKind.UnitType);
                    if (knownVariables.Length == 1)
                    {
                        paramTypes = knownVariables.First().Type;
                    }
                    else if (knownVariables.Length > 1)
                    {
                        paramTypes = ResolvedType.New(ResolvedTypeKind.NewTupleType(knownVariables
                            .Select(var => var.Type)
                            .ToImmutableArray()));
                    }

                    var (signature, specializations) = MakeSpecializations(newName, paramTypes, SpecializationImplementation.NewProvided(parameters, contents));

                    var controlCallable = new QsCallable(
                        QsCallableKind.Operation,
                        newName,
                        ImmutableArray<QsDeclarationAttribute>.Empty,
                        CurrentCallable.Callable.SourceFile,
                        QsNullable<QsLocation>.Null,
                        signature,
                        parameters,
                        specializations.ToImmutableArray(),
                        ImmutableArray<string>.Empty,
                        QsComments.Empty);

                    var updatedCallable = UpdateGeneratedOp.Apply(controlCallable, knownVariables, CurrentCallable.Callable.FullName, newName);

                    return (updatedCallable, signature.ArgumentType);
                }
            }

            private HoistContents() : base(new TransformationState())
            {
                this.Namespaces = new NamespaceTransformation(this);
                this.StatementKinds = new StatementKindTransformation(this);
                this.Expressions = new ExpressionTransformation(this);
                this.ExpressionKinds = new ExpressionKindTransformation(this);
            }

            private class NamespaceTransformation : NamespaceTransformation<TransformationState>
            {
                public NamespaceTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                public override QsCallable onCallableImplementation(QsCallable c)
                {
                    Transformation.InternalState.CurrentCallable = new CallableDetails(c);
                    return base.onCallableImplementation(c);
                }

                public override QsSpecialization onBodySpecialization(QsSpecialization spec)
                {
                    Transformation.InternalState.InBody = true;
                    var rtrn = base.onBodySpecialization(spec);
                    Transformation.InternalState.InBody = false;
                    return rtrn;
                }

                public override QsSpecialization onAdjointSpecialization(QsSpecialization spec)
                {
                    Transformation.InternalState.InAdjoint = true;
                    var rtrn = base.onAdjointSpecialization(spec);
                    Transformation.InternalState.InAdjoint = false;
                    return rtrn;
                }

                public override QsSpecialization onControlledSpecialization(QsSpecialization spec)
                {
                    Transformation.InternalState.InControlled = true;
                    var rtrn = base.onControlledSpecialization(spec);
                    Transformation.InternalState.InControlled = false;
                    return rtrn;
                }

                public override QsCallable onFunction(QsCallable c) => c; // Prevent anything in functions from being hoisted

                public override QsNamespace Transform(QsNamespace ns)
                {
                    // Control operations list will be populated in the transform
                    Transformation.InternalState.ControlOperations = new List<QsCallable>();
                    return base.Transform(ns)
                        .WithElements(elems => elems.AddRange(Transformation.InternalState.ControlOperations.Select(op => QsNamespaceElement.NewQsCallable(op))));
                }
            }

            private class StatementKindTransformation : Core.StatementKindTransformation<TransformationState>
            {
                public StatementKindTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                private (QsCallable, QsStatement) HoistBody(QsScope body)
                {
                    var (targetOp, originalArgumentType) = Transformation.InternalState.GenerateOperation(body);
                    var targetOpType = ResolvedType.New(ResolvedTypeKind.NewOperation(
                        Tuple.Create(
                            originalArgumentType,
                            ResolvedType.New(ResolvedTypeKind.UnitType)),
                        targetOp.Signature.Information));

                    var targetTypeArgTypes = Transformation.InternalState.CurrentCallable.TypeParamTypes;
                    var targetOpId = new TypedExpression
                    (
                        ExpressionKind.NewIdentifier(Identifier.NewGlobalCallable(targetOp.FullName), targetTypeArgTypes),
                        targetTypeArgTypes.IsNull
                            ? TypeArgsResolution.Empty
                            : targetTypeArgTypes.Item
                                .Select(type => Tuple.Create(targetOp.FullName, ((ResolvedTypeKind.TypeParameter)type.Resolution).Item.TypeName, type))
                                .ToImmutableArray(),
                        targetOpType,
                        new InferredExpressionInformation(false, false),
                        QsNullable<Tuple<QsPositionInfo, QsPositionInfo>>.Null
                    );

                    var knownSymbols = body.KnownSymbols.Variables;

                    TypedExpression targetArgs = null;
                    if (knownSymbols.Any())
                    {
                        targetArgs = Utilities.CreateValueTupleExpression(knownSymbols.Select(var => Utilities.CreateIdentifierExpression(
                            Identifier.NewLocalVariable(var.VariableName),
                            TypeArgsResolution.Empty,
                            var.Type))
                            .ToArray());
                    }
                    else
                    {
                        targetArgs = new TypedExpression
                        (
                            ExpressionKind.UnitValue,
                            TypeArgsResolution.Empty,
                            ResolvedType.New(ResolvedTypeKind.UnitType),
                            new InferredExpressionInformation(false, false),
                            QsNullable<Tuple<QsPositionInfo, QsPositionInfo>>.Null
                        );
                    }

                    var call = new TypedExpression
                    (
                        ExpressionKind.NewCallLikeExpression(targetOpId, targetArgs),
                        targetTypeArgTypes.IsNull
                            ? TypeArgsResolution.Empty
                            : targetTypeArgTypes.Item
                                .Select(type => Tuple.Create(Transformation.InternalState.CurrentCallable.Callable.FullName, ((ResolvedTypeKind.TypeParameter)type.Resolution).Item.TypeName, type))
                                .ToImmutableArray(),
                        ResolvedType.New(ResolvedTypeKind.UnitType),
                        new InferredExpressionInformation(false, true),
                        QsNullable<Tuple<QsPositionInfo, QsPositionInfo>>.Null
                    );

                    return (targetOp, new QsStatement(
                        QsStatementKind.NewQsExpressionStatement(call),
                        LocalDeclarations.Empty,
                        QsNullable<QsLocation>.Null,
                        QsComments.Empty));
                }

                // ToDo: This logic should be externalized at some point to make the Hoisting more general
                private bool IsScopeSingleCall(QsScope contents)
                {
                    if (contents.Statements.Length != 1) return false;

                    return contents.Statements[0].Statement is QsStatementKind.QsExpressionStatement expr
                           && expr.Item.Expression is ExpressionKind.CallLikeExpression call
                           && !TypedExpression.IsPartialApplication(expr.Item.Expression)
                           && call.Item1.Expression is ExpressionKind.Identifier;
                }

                public override QsStatementKind onConjugation(QsConjugation stm)
                {
                    var superInWithinBlock = Transformation.InternalState.InWithinBlock;
                    Transformation.InternalState.InWithinBlock = true;
                    var (_, outer) = this.onPositionedBlock(QsNullable<TypedExpression>.Null, stm.OuterTransformation);
                    Transformation.InternalState.InWithinBlock = superInWithinBlock;

                    var (_, inner) = this.onPositionedBlock(QsNullable<TypedExpression>.Null, stm.InnerTransformation);

                    return QsStatementKind.NewQsConjugation(new QsConjugation(outer, inner));
                }

                public override QsStatementKind onReturnStatement(TypedExpression ex)
                {
                    Transformation.InternalState.IsValidScope = false;
                    return base.onReturnStatement(ex);
                }

                public override QsStatementKind onValueUpdate(QsValueUpdate stm)
                {
                    // If lhs contains an identifier found in the scope's known variables (variables from the super-scope), the scope is not valid
                    var lhs = this.Expressions.Transform(stm.Lhs);

                    if (Transformation.InternalState.ContainsHoistParamRef)
                    {
                        Transformation.InternalState.IsValidScope = false;
                    }

                    var rhs = this.Expressions.Transform(stm.Rhs);
                    return QsStatementKind.NewQsValueUpdate(new QsValueUpdate(lhs, rhs));
                }

                public override QsStatementKind onConditionalStatement(QsConditionalStatement stm)
                {
                    var contextValidScope = Transformation.InternalState.IsValidScope;
                    var contextHoistParams = Transformation.InternalState.CurrentHoistParams;

                    var isHoistValid = true;

                    var newConditionBlocks = new List<Tuple<TypedExpression, QsPositionedBlock>>();
                    var generatedOperations = new List<QsCallable>();
                    foreach (var conditionBlock in stm.ConditionalBlocks)
                    {
                        Transformation.InternalState.IsValidScope = true;
                        Transformation.InternalState.CurrentHoistParams = conditionBlock.Item2.Body.KnownSymbols.IsEmpty
                        ? ImmutableArray<LocalVariableDeclaration<NonNullable<string>>>.Empty
                        : conditionBlock.Item2.Body.KnownSymbols.Variables;

                        var (expr, block) = this.onPositionedBlock(QsNullable<TypedExpression>.NewValue(conditionBlock.Item1), conditionBlock.Item2);

                        // ToDo: Reduce the number of unnecessary generated operations by generalizing
                        // the condition logic for the conversion and using that condition here
                        //var (isExprCondition, _, _) = IsConditionedOnResultLiteralExpression(expr.Item);

                        if (Transformation.InternalState.IsValidScope) // if sub-scope is valid, hoist content
                        {
                            if (/*isExprCondition &&*/ !IsScopeSingleCall(block.Body))
                            {
                                // Hoist the scope to its own operation
                                var (callable, call) = HoistBody(block.Body);
                                block = new QsPositionedBlock(
                                    new QsScope(ImmutableArray.Create(call), block.Body.KnownSymbols),
                                    block.Location,
                                    block.Comments);
                                newConditionBlocks.Add(Tuple.Create(expr.Item, block));
                                generatedOperations.Add(callable);
                            }
                            else if(block.Body.Statements.Length > 0)
                            {
                                newConditionBlocks.Add(Tuple.Create(expr.Item, block));
                            }
                        }
                        else
                        {
                            isHoistValid = false;
                            break;
                        }
                    }

                    var newDefault = QsNullable<QsPositionedBlock>.Null;
                    if (isHoistValid && stm.Default.IsValue)
                    {
                        Transformation.InternalState.IsValidScope = true;
                        Transformation.InternalState.CurrentHoistParams = stm.Default.Item.Body.KnownSymbols.IsEmpty
                            ? ImmutableArray<LocalVariableDeclaration<NonNullable<string>>>.Empty
                            : stm.Default.Item.Body.KnownSymbols.Variables;

                        var (_, block) = this.onPositionedBlock(QsNullable<TypedExpression>.Null, stm.Default.Item);
                        if (Transformation.InternalState.IsValidScope)
                        {
                            if (!IsScopeSingleCall(block.Body)) // if sub-scope is valid, hoist content
                            {
                                // Hoist the scope to its own operation
                                var (callable, call) = HoistBody(block.Body);
                                block = new QsPositionedBlock(
                                    new QsScope(ImmutableArray.Create(call), block.Body.KnownSymbols),
                                    block.Location,
                                    block.Comments);
                                newDefault = QsNullable<QsPositionedBlock>.NewValue(block);
                                generatedOperations.Add(callable);
                            }
                            else if(block.Body.Statements.Length > 0)
                            {
                                newDefault = QsNullable<QsPositionedBlock>.NewValue(block);
                            }
                        }
                        else
                        {
                            isHoistValid = false;
                        }
                    }

                    if (isHoistValid)
                    {
                        Transformation.InternalState.ControlOperations.AddRange(generatedOperations);
                    }

                    Transformation.InternalState.CurrentHoistParams = contextHoistParams;
                    Transformation.InternalState.IsValidScope = contextValidScope;

                    return isHoistValid
                        ? QsStatementKind.NewQsConditionalStatement(
                          new QsConditionalStatement(newConditionBlocks.ToImmutableArray(), newDefault))
                        : QsStatementKind.NewQsConditionalStatement(
                          new QsConditionalStatement(stm.ConditionalBlocks, stm.Default));
                }

                public override QsStatementKind Transform(QsStatementKind kind)
                {
                    Transformation.InternalState.ContainsHoistParamRef = false; // Every statement kind starts off false
                    return base.Transform(kind);
                }
            }

            private class ExpressionTransformation : Core.ExpressionTransformation<TransformationState>
            {
                public ExpressionTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                public override TypedExpression Transform(TypedExpression ex)
                {
                    var contextContainsHoistParamRef = Transformation.InternalState.ContainsHoistParamRef;
                    Transformation.InternalState.ContainsHoistParamRef = false;
                    var rtrn = base.Transform(ex);

                    // If the sub context contains a reference, then the super context contains a reference,
                    // otherwise return the super context to its original value
                    if (!Transformation.InternalState.ContainsHoistParamRef)
                    {
                        Transformation.InternalState.ContainsHoistParamRef = contextContainsHoistParamRef;
                    }

                    return rtrn;
                }
            }

            private class ExpressionKindTransformation : Core.ExpressionKindTransformation<TransformationState>
            {
                public ExpressionKindTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                public override ExpressionKind onIdentifier(Identifier sym, QsNullable<ImmutableArray<ResolvedType>> tArgs)
                {
                    if (sym is Identifier.LocalVariable local &&
                    Transformation.InternalState.CurrentHoistParams.Any(param => param.VariableName.Equals(local.Item)))
                    {
                        Transformation.InternalState.ContainsHoistParamRef = true;
                    }
                    return base.onIdentifier(sym, tArgs);
                }
            }
        }

        // Transformation that updates the contents of newly generated operations by:
        // 1. Rerouting the origins of type parameter references to the new operation
        // 2. Changes the IsMutable info on variable that used to be mutable, but are now immutable params to the operation
        private class UpdateGeneratedOp : QsSyntaxTreeTransformation<UpdateGeneratedOp.TransformationState>
        {
            public static QsCallable Apply(QsCallable qsCallable, ImmutableArray<LocalVariableDeclaration<NonNullable<string>>> parameters, QsQualifiedName oldName, QsQualifiedName newName)
            {
                var filter = new UpdateGeneratedOp(parameters, oldName, newName);

                return filter.Namespaces.onCallableImplementation(qsCallable);
            }

            public class TransformationState
            {
                public bool IsRecursiveIdentifier = false;
                public readonly ImmutableArray<LocalVariableDeclaration<NonNullable<string>>> Parameters;
                public readonly QsQualifiedName OldName;
                public readonly QsQualifiedName NewName;

                public TransformationState(ImmutableArray<LocalVariableDeclaration<NonNullable<string>>> parameters, QsQualifiedName oldName, QsQualifiedName newName)
                {
                    Parameters = parameters;
                    OldName = oldName;
                    NewName = newName;
                }
            }

            private UpdateGeneratedOp(ImmutableArray<LocalVariableDeclaration<NonNullable<string>>> parameters, QsQualifiedName oldName, QsQualifiedName newName)
            : base(new TransformationState(parameters, oldName, newName))
            {
                this.Expressions = new ExpressionTransformation(this);
                this.ExpressionKinds = new ExpressionKindTransformation(this);
                this.Types = new TypeTransformation(this);
            }

            private class ExpressionTransformation : Core.ExpressionTransformation<TransformationState>
            {
                public ExpressionTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                public override ImmutableDictionary<Tuple<QsQualifiedName, NonNullable<string>>, ResolvedType> onTypeParamResolutions(ImmutableDictionary<Tuple<QsQualifiedName, NonNullable<string>>, ResolvedType> typeParams)
                {
                    // Prevent keys from having their names updated
                    return typeParams.ToImmutableDictionary(kvp => kvp.Key, kvp => this.Types.Transform(kvp.Value));
                }

                public override TypedExpression Transform(TypedExpression ex)
                {
                    // Checks if expression is mutable identifier that is in parameter list
                    if (ex.InferredInformation.IsMutable &&
                        ex.Expression is ExpressionKind.Identifier id &&
                        id.Item1 is Identifier.LocalVariable variable &&
                        Transformation.InternalState.Parameters.Any(x => x.VariableName.Equals(variable)))
                    {
                        // Set the mutability to false
                        ex = new TypedExpression(
                            ex.Expression,
                            ex.TypeArguments,
                            ex.ResolvedType,
                            new InferredExpressionInformation(false, ex.InferredInformation.HasLocalQuantumDependency),
                            ex.Range);
                    }

                    // Prevent IsRecursiveIdentifier from propagating beyond the typed expression it is referring to
                    var isRecursiveIdentifier = Transformation.InternalState.IsRecursiveIdentifier;
                    var rtrn = base.Transform(ex);
                    Transformation.InternalState.IsRecursiveIdentifier = isRecursiveIdentifier;
                    return rtrn;
                }
            }

            private class ExpressionKindTransformation : Core.ExpressionKindTransformation<TransformationState>
            {
                public ExpressionKindTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                public override ExpressionKind onIdentifier(Identifier sym, QsNullable<ImmutableArray<ResolvedType>> tArgs)
                {
                    var rtrn = base.onIdentifier(sym, tArgs);

                    // Then check if this is a recursive identifier
                    // In this context, that is a call back to the original callable from the newly generated operation
                    if (sym is Identifier.GlobalCallable callable && Transformation.InternalState.OldName.Equals(callable.Item))
                    {
                        // Setting this flag will prevent the rerouting logic from processing the resolved type of the recursive identifier expression.
                        // This is necessary because we don't want any type parameters from the original callable from being rerouted to the new generated
                        // operation's type parameters in the definition of the identifier.
                        Transformation.InternalState.IsRecursiveIdentifier = true;
                    }
                    return rtrn;
                }
            }

            private class TypeTransformation : TypeTransformation<TransformationState>
            {
                public TypeTransformation(QsSyntaxTreeTransformation<TransformationState> parent) : base(parent) { }

                public override ResolvedTypeKind onTypeParameter(QsTypeParameter tp)
                {
                    // Reroute a type parameter's origin to the newly generated operation
                    if (!Transformation.InternalState.IsRecursiveIdentifier && Transformation.InternalState.OldName.Equals(tp.Origin))
                    {
                        tp = new QsTypeParameter(Transformation.InternalState.NewName, tp.TypeName, tp.Range);
                    }

                    return base.onTypeParameter(tp);
                }
            }
        }
    }
}
