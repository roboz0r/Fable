module rec Fable.Transforms.Fable2Dart

open System.Collections.Generic
open Fable
open Fable.AST
open Fable.AST.Dart
open Fable.Transforms.AST

type ReturnStrategy =
    | Return
    | ReturnVoid
    | Assign of Expression
    | Target of Ident

type ArgsInfo =
    | CallInfo of Fable.CallInfo
    | NoCallInfo of args: Fable.Expr list

type ITailCallOpportunity =
    abstract Label: string
    abstract Args: string list
    abstract IsRecursiveRef: Fable.Expr -> bool

type UsedNames =
  { RootScope: HashSet<string>
    DeclarationScopes: HashSet<string>
    CurrentDeclarationScope: HashSet<string> }

type Context =
  { File: Fable.File
    UsedNames: UsedNames
    DecisionTargets: (Fable.Ident list * Fable.Expr) list
    HoistVars: Fable.Ident list -> bool
    TailCallOpportunity: ITailCallOpportunity option
    OptimizeTailCall: unit -> unit }

type MemberKind =
    | ClassConstructor
    | NonAttached of funcName: string
    | Attached of isStatic: bool

type IDartCompiler =
    inherit Compiler
    abstract GetAllImports: unit -> Import list
    abstract GetImportIdent: Context * selector: string * path: string * ?range: SourceLocation -> Ident
    abstract TransformType: Context * Fable.Type -> Type
    abstract TransformAsExpr: Context * Fable.Expr -> Expression
    abstract TransformAsStatements: Context * ReturnStrategy option * Fable.Expr -> Statement list
    abstract TransformFunction: Context * string option * Fable.Ident list * Fable.Expr -> Ident list * Statement list
    abstract WarnOnlyOnce: string * ?range: SourceLocation -> unit

module Util =
    let (|TransformExpr|) (com: IDartCompiler) ctx e =
        com.TransformAsExpr(ctx, e)

    let (|TransformType|) (com: IDartCompiler) ctx e =
        com.TransformType(ctx, e)

    let (|Function|_|) = function
        | Fable.Lambda(arg, body, _) -> Some([arg], body)
        | Fable.Delegate(args, body, _) -> Some(args, body)
        | _ -> None

    let (|Lets|_|) = function
        | Fable.Let(ident, value, body) -> Some([ident, value], body)
        | Fable.LetRec(bindings, body) -> Some(bindings, body)
        | _ -> None

    let makeTypeRef ident genArgs =
        TypeReference(ident, genArgs)

    let makeTypeRefFromName typeName genArgs =
        let ident = makeIdent MetaType typeName
        makeTypeRef ident genArgs

    let libValue (com: IDartCompiler) ctx moduleName memberName =
        com.GetImportIdent(ctx, memberName, getLibPath com moduleName)

    let libTypeRef (com: IDartCompiler) ctx moduleName memberName genArgs =
        let ident = libValue com ctx moduleName memberName
        makeTypeRef ident genArgs

    let libCall (com: IDartCompiler) ctx moduleName memberName args =
        let fn = com.GetImportIdent(ctx, memberName, getLibPath com moduleName)
        Expression.invocationExpression(fn.Expr, args)

    let extLibCall (com: IDartCompiler) ctx modulePath memberName args =
        let fn = com.GetImportIdent(ctx, memberName, modulePath)
        Expression.invocationExpression(fn.Expr, args)

    let sequenceExpression (com: IDartCompiler) ctx exprs returnExpr =
        let exprs = Expression.listLiteral exprs
        libCall com ctx "Util" "sequenceExpression" [exprs; returnExpr]

    let discardUnitArg (args: Fable.Ident list) =
        match args with
        | [] -> []
        | [unitArg] when unitArg.Type = Fable.Unit -> []
        | [thisArg; unitArg] when thisArg.IsThisArgument && unitArg.Type = Fable.Unit -> [thisArg]
        | args -> args

    let addErrorAndReturnNull (com: Compiler) (range: SourceLocation option) (error: string) =
        addError com [] range error
        NullLiteral |> Literal

    let numType kind = Fable.Number(kind, Fable.NumberInfo.Empty)

    let makeIdent typ name =
        { Name = name; Type = typ; Prefix = None }

    let makePrefixedIdent typ prefix name =
        { Name = name; Type = typ; Prefix = Some prefix }

    let makeReturnBlock expr =
        [Statement.returnStatement expr]

    let getEntityRef (com: IDartCompiler) ctx ent =
        let entRef = Dart.Replacements.entityRef com ent
        match com.TransformAsExpr(ctx, entRef) with
        | IdentExpression ident -> ident
        | _ -> failwith $"Unexpected, entity ref for {ent.FullName} is not an identifer"

    // TODO: Check conversions like ToString > toString
    let get left memberName =
        PropertyAccess(left, memberName)

    let getExpr left expr =
        IndexExpression(left, expr)

    let getUnionExprTag expr =
        get expr "tag"

    let getUnionExprFields expr =
        get expr "fields"

    let rec getParts (parts: string list) (expr: Expression) =
        match parts with
        | [] -> expr
        | m::ms -> get expr m |> getParts ms

    let getUniqueNameInRootScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name)
            || ctx.UsedNames.DeclarationScopes.Contains(name))
        ctx.UsedNames.RootScope.Add(name) |> ignore
        name

    let getUniqueNameInDeclarationScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name) || ctx.UsedNames.CurrentDeclarationScope.Contains(name))
        ctx.UsedNames.CurrentDeclarationScope.Add(name) |> ignore
        name

    type NamedTailCallOpportunity(_com: Compiler, ctx, name, args: Fable.Ident list) =
        // Capture the current argument values to prevent delayed references from getting corrupted,
        // for that we use block-scoped ES2015 variable declarations. See #681, #1859
        // TODO: Local unique ident names
        let argIds = discardUnitArg args |> List.map (fun arg ->
            getUniqueNameInDeclarationScope ctx (arg.Name + "_mut"))
        interface ITailCallOpportunity with
            member _.Label = name
            member _.Args = argIds
            member _.IsRecursiveRef(e) =
                match e with Fable.IdentExpr id -> name = id.Name | _ -> false

    let getDecisionTarget (ctx: Context) targetIndex =
        match List.tryItem targetIndex ctx.DecisionTargets with
        | None -> failwithf $"Cannot find DecisionTree target %i{targetIndex}"
        | Some(idents, target) -> idents, target

    let rec isStatement ctx preferStatement (expr: Fable.Expr) =
        match expr with
        | Fable.Value(v,_) ->
            match v with
            | Fable.UnitConstant _ -> true
            | _ -> false

        | Fable.Unresolved _
        | Fable.Import _  | Fable.IdentExpr _
        | Fable.Lambda _ | Fable.Delegate _ | Fable.ObjectExpr _
        | Fable.Call _ | Fable.CurriedApply _ | Fable.Operation _
        | Fable.Get _ | Fable.Test _ | Fable.TypeCast _ -> false

        | Fable.Set _
        | Fable.Let _
        | Fable.LetRec _
        | Fable.Sequential _
        | Fable.TryCatch _
        | Fable.ForLoop _
        | Fable.WhileLoop _ -> true

        | Fable.Extended(kind, _) ->
            match kind with
            | Fable.RegionStart _ -> true
            | Fable.Throw _
            | Fable.Debugger _
            | Fable.Curry _ -> false

        // TODO: If IsSatement is false, still try to infer it? See #2414
        // /^\s*(break|continue|debugger|while|for|switch|if|try|let|const|var)\b/
        | Fable.Emit(i,_,_) -> i.IsStatement

        | Fable.DecisionTreeSuccess(targetIndex,_, _) ->
            getDecisionTarget ctx targetIndex
            |> snd |> isStatement ctx preferStatement

        // Make it also statement if we have more than, say, 3 targets?
        // That would increase the chances to convert it into a switch
        | Fable.DecisionTree(_,targets) ->
            preferStatement
            || List.exists (snd >> (isStatement ctx false)) targets

        | Fable.IfThenElse(_,thenExpr,elseExpr,_) ->
            preferStatement || elseExpr.Type = Fable.Unit || isStatement ctx false thenExpr || isStatement ctx false elseExpr

    let isInt64OrLess = function
        | Fable.Number(kind, _) ->
            match kind with
            | Int8 | UInt8 | Int16 | UInt16 | Int32 | UInt32 | Int64 | UInt64 -> true
            | Float32 | Float64 | Decimal | NativeInt | UNativeInt | BigInt -> false
        | _ -> false

    let assign (_range: SourceLocation option) left right =
        AssignmentExpression(left, AssignEqual, right)

    /// Immediately Invoked Function Expression
    let iife (com: IDartCompiler) ctx (expr: Fable.Expr) =
        let args, body = com.TransformFunction(ctx, None, [], expr)
        let fn = Expression.anonymousFunction(args, body)
        Expression.invocationExpression(fn, [])

    // let optimizeTailCall (com: IDartCompiler) (ctx: Context) range (tc: ITailCallOpportunity) args =
    //     let rec checkCrossRefs tempVars allArgs = function
    //         | [] -> tempVars
    //         | (argId, _arg)::rest ->
    //             let found = allArgs |> List.exists (FableTransforms.deepExists (function
    //                 | Fable.IdentExpr i -> argId = i.Name
    //                 | _ -> false))
    //             let tempVars =
    //                 if found then
    //                     let tempVarName = getUniqueNameInDeclarationScope ctx (argId + "_tmp")
    //                     Map.add argId tempVarName tempVars
    //                 else tempVars
    //             checkCrossRefs tempVars allArgs rest
    //     ctx.OptimizeTailCall()
    //     let zippedArgs = List.zip tc.Args args
    //     let tempVars = checkCrossRefs Map.empty args zippedArgs
    //     let tempVarReplacements = tempVars |> Map.map (fun _ v -> makeIdentExpr v)
    //     [|
    //         // First declare temp variables
    //         for (KeyValue(argId, tempVar)) in tempVars do
    //             yield varDeclaration (Pattern.identifier(tempVar)) false (Expression.identifier(argId)) |> Declaration.VariableDeclaration |> Declaration
    //         // Then assign argument expressions to the original argument identifiers
    //         // See https://github.com/fable-compiler/Fable/issues/1368#issuecomment-434142713
    //         for (argId, arg) in zippedArgs do
    //             let arg = FableTransforms.replaceValues tempVarReplacements arg
    //             let arg = com.TransformAsExpr(ctx, arg)
    //             yield assign None (Expression.identifier(argId)) arg |> ExpressionStatement
    //         yield Statement.continueStatement(Identifier.identifier(tc.Label), ?loc=range)
    //     |]

    let transformCallArgs (com: IDartCompiler) ctx (r: SourceLocation option) (info: ArgsInfo) =
        // TODO: Named params
        let paramObjInfo, hasSpread, args =
            match info with
            | CallInfo i ->
                let paramObjInfo = None // TODO
                paramObjInfo, i.HasSpread, i.Args
            | NoCallInfo args -> None, false, args

        match args with
        | []
        | [MaybeCasted(Fable.Value(Fable.UnitConstant,_))] -> []
        // | args when hasSpread ->
        //     match List.rev args with
        //     | [] -> []
        //     | (Replacements.Util.ArrayOrListLiteral(spreadArgs,_))::rest ->
        //         let rest = List.rev rest |> List.map (fun e -> com.TransformAsExpr(ctx, e))
        //         rest @ (List.map (fun e -> com.TransformAsExpr(ctx, e)) spreadArgs)
        //     | last::rest ->
        //         let rest = List.rev rest |> List.map (fun e -> com.TransformAsExpr(ctx, e))
        //         rest @ [Expression.spreadElement(com.TransformAsExpr(ctx, last))]
        | args -> List.map (fun e -> com.TransformAsExpr(ctx, e)) args

    let resolveExpr strategy expr: Statement =
        match strategy with
        | None | Some ReturnVoid -> ExpressionStatement expr
        | Some Return -> ReturnStatement expr
        | Some(Assign left) -> assign None left expr |> ExpressionStatement
        | Some(Target left) -> assign None (IdentExpression left) expr |> ExpressionStatement

    let rec transformType (com: IDartCompiler) (ctx: Context) (t: Fable.Type) =
        match t with
        | Fable.Measure _
        | Fable.Any -> Object
        | Fable.Unit -> Void
        | Fable.MetaType -> MetaType
        | Fable.Boolean -> Boolean
        | Fable.String -> String
        | Fable.Char -> Integer
        | Fable.Number(kind, _) ->
            match kind with
            | Int8 | UInt8 | Int16 | UInt16 | Int32 | UInt32 | Int64 | UInt64 -> Integer
            | Float32 | Float64 -> Double
            | Decimal | BigInt | NativeInt | UNativeInt -> Dynamic // TODO
        | Fable.Option(TransformType com ctx genArg, _isStruct) -> Nullable genArg
        | Fable.Array(TransformType com ctx genArg) -> List genArg
        | Fable.List(TransformType com ctx genArg) ->
            TypeReference(libValue com ctx "List" "List", [genArg])
        | Fable.Tuple(genArgs, _isStruct) ->
            let tup = com.GetImportIdent(ctx, $"Tuple{genArgs.Length}", "package:tuple/tuple.dart")
            let genArgs = genArgs |> List.map (transformType com ctx)
            TypeReference(tup, genArgs)
        | Fable.LambdaType(TransformType com ctx argType, TransformType com ctx returnType) ->
            Function([argType], returnType)
        | Fable.DelegateType(argTypes, TransformType com ctx returnType) ->
            let argTypes = argTypes |> List.map (transformType com ctx)
            Function(argTypes, returnType)
        | Fable.GenericParam(name, _constraints) -> Generic name
        | Fable.DeclaredType(ref, genArgs) ->
            let ent = com.GetEntity(ref)
            // TODO: Discard measure types
            let genArgs = genArgs |> List.map (transformType com ctx)
            TypeReference(getEntityRef com ctx ent, genArgs)
        | Fable.AnonymousRecordType _
        | Fable.Regex -> Dynamic // TODO

    let transformIdentWith (com: IDartCompiler) ctx typ name: Ident =
        let typ = transformType com ctx typ
        makeIdent typ name

    let transformIdent (com: IDartCompiler) ctx (id: Fable.Ident): Ident =
        transformIdentWith com ctx id.Type id.Name

    let transformIdentAsExpr (com: IDartCompiler) ctx (id: Fable.Ident) =
        transformIdentWith com ctx id.Type id.Name |> Expression.identExpression

    let transformVarDeclaration com ctx (memb: Fable.MemberDecl) =
        // TODO: Prefix non-public values with underscore or raise warning?
        let ident = transformIdentWith com ctx memb.Body.Type memb.Name
        // TODO: If value is primitive, list, union or record without mutable fields
        // we can declare it as const (if var is mutable we can make only the value const)
        let kind = if memb.Info.IsMutable then Var else Final
        let value = transformAsExpr com ctx memb.Body
        Declaration.variableDeclaration(ident, kind, value)

    let transformLocalVarDeclaration com ctx (fableIdent: Fable.Ident) value =
        let ident = transformIdent com ctx fableIdent
        // TODO: If value is primitive, list, union or record without mutable fields
        // we can declare it as const (if var is mutable we can make only the value const)
        let kind = if fableIdent.IsMutable then Var else Final
        ident, Statement.variableDeclaration(ident, kind, ?value=value)

    let transformImport (com: IDartCompiler) ctx r (selector: string) (path: string) =
        let selector, parts =
            let parts = Array.toList(selector.Split('.'))
            parts.Head, parts.Tail
        com.GetImportIdent(ctx, selector, path, ?range=r)
        |> Expression.identExpression
        |> getParts parts

    let transformValue (com: IDartCompiler) (ctx: Context) (r: SourceLocation option) value: Expression =
        match value with
        | Fable.ThisValue _ -> ThisExpression
        | Fable.BaseValue(None,_) -> SuperExpression
        | Fable.BaseValue(Some boundIdent,_) -> transformIdentAsExpr com ctx boundIdent
        | Fable.TypeInfo(t, d) -> transformType com ctx t |> TypeLiteral
        | Fable.Null _t -> Expression.nullLiteral()
        | Fable.UnitConstant -> libCall com ctx "Util" "ignore" []
        | Fable.BoolConstant v -> Expression.booleanLiteral v
        | Fable.CharConstant v -> Expression.integerLiteral(int v)
        | Fable.StringConstant v -> Expression.stringLiteral v
        | Fable.StringTemplate _ ->
            "TODO: StringTemplate is not supported yet"
            |> addErrorAndReturnNull com r
        | Fable.NumberConstant(x, kind, _) ->
            match kind, x with
            | Int8, (:? int8 as x) -> Expression.integerLiteral(int64 x)
            | UInt8, (:? uint8 as x) -> Expression.integerLiteral(int64 x)
            | Int16, (:? int16 as x) -> Expression.integerLiteral(int64 x)
            | UInt16, (:? uint16 as x) -> Expression.integerLiteral(int64 x)
            | Int32, (:? int32 as x) -> Expression.integerLiteral(x)
            | UInt32, (:? uint32 as x) -> Expression.integerLiteral(int64 x)
            | Int64, (:? int64 as x) -> Expression.integerLiteral(x)
            | UInt64, (:? uint64 as x) -> Expression.integerLiteral(int64 x)
            | Float32, (:? float32 as x) -> Expression.doubleLiteral(float x)
            | Float64, (:? float as x) -> Expression.doubleLiteral(x)
            | _ ->
                $"Expected literal of type %A{kind} but got {x.GetType().FullName}"
                |> addErrorAndReturnNull com r
        | Fable.RegexConstant _ ->
            "TODO: RegexConstant is not supported yet"
            |> addErrorAndReturnNull com r
        | Fable.NewArray (values, typ) ->
            values
            |> List.map (transformAsExpr com ctx)
            // TODO: Check if we can make it const
            |> Expression.listLiteral
        // TODO: Use List.filled for allocation and List.from for other expressions
        | Fable.NewArrayFrom (TransformExpr com ctx size, typ) ->
            let ident = makeIdent MetaType "List"
            Expression.invocationExpression(ident.Expr, [size], genArgs=[transformType com ctx typ])
        | Fable.NewTuple(vals,_) ->
            let tup = com.GetImportIdent(ctx, $"Tuple{vals.Length}", "package:tuple/tuple.dart")
            let vals = vals |> List.map (transformAsExpr com ctx)
            // Generic arguments can be omitted
//            let genArgs = vals |> List.map (fun v -> transformType com ctx v.Type)
            Expression.invocationExpression(tup.Expr, vals)
        // TODO: optimization for nested constructors
        | Fable.NewList(headAndTail, typ) ->
            let list = libValue com ctx "List" "List"
            let fn, args =
                match headAndTail with
                | None -> Expression.propertyAccess(list.Expr, "empty"), []
                | Some(TransformExpr com ctx head, TransformExpr com ctx tail) ->
                    list.Expr, [head; tail]
            Expression.invocationExpression(fn, args, genArgs=[transformType com ctx typ])
        | Fable.NewOption(None, _typ, _isStruct) -> Expression.nullLiteral()
        | Fable.NewOption(Some v, _typ, _isStruct) -> transformAsExpr com ctx v
        | Fable.NewRecord(values, ref, genArgs) ->
            let ent = com.GetEntity(ref)
            let args = values |> List.map (transformAsExpr com ctx)
            let genArgs = genArgs |> List.map (transformType com ctx)
            let consRef = getEntityRef com ctx ent
            Expression.invocationExpression(consRef.Expr, args, genArgs=genArgs)
        | Fable.NewAnonymousRecord _ ->
            "TODO: Anonymous record is not supported yet"
            |> addErrorAndReturnNull com r
        | Fable.NewUnion(values, tag, ref, _genArgs) ->
            let ent = com.GetEntity(ref)
            let fields = List.map (fun x -> com.TransformAsExpr(ctx, x)) values
            let args = [Expression.integerLiteral(tag); Expression.listLiteral fields]
            let consRef = getEntityRef com ctx ent
            Expression.invocationExpression(consRef.Expr, args)

    let transformOperation com ctx (_: SourceLocation option) t opKind: Expression =
        match opKind with
        | Fable.Unary(op, TransformExpr com ctx expr) ->
            UnaryExpression(op, expr)
        | Fable.Binary(op, TransformExpr com ctx left, TransformExpr com ctx right) ->
            BinaryExpression(op, left, right, isInt64OrLess t)
        | Fable.Logical(op, TransformExpr com ctx left, TransformExpr com ctx right) ->
            LogicalExpression(op, left, right)

    let transformEmit (com: IDartCompiler) ctx range (info: Fable.EmitInfo) =
        let macro = info.Macro
        let info = info.CallInfo
        let thisArg = info.ThisArg |> Option.map (fun e -> com.TransformAsExpr(ctx, e)) |> Option.toList
        let args =
            transformCallArgs com ctx range (CallInfo info)
            |> List.append thisArg
        Expression.emitExpression(macro, args)

    let transformCall (com: IDartCompiler) ctx range callee (callInfo: Fable.CallInfo) =
        // Try to optimize some patterns after FableTransforms
        let optimized =
            match callInfo.OptimizableInto, callInfo.Args with
            | Some "array" , [Replacements.Util.ArrayOrListLiteral(vals,_)] -> Fable.Value(Fable.NewArray(vals, Fable.Any), range) |> Some
            | _ -> None

        match optimized with
        | Some e -> com.TransformAsExpr(ctx, e)
        | None ->
            let callee = com.TransformAsExpr(ctx, callee)
            let args = transformCallArgs com ctx range (CallInfo callInfo)
            match callInfo.ThisArg with
            | Some(TransformExpr com ctx thisArg) -> Expression.invocationExpression(callee, thisArg::args)
            | None -> Expression.invocationExpression(callee, args)

    let transformCurriedApply com ctx range (TransformExpr com ctx applied) args =
        match transformCallArgs com ctx range (NoCallInfo args) with
        | [] -> Expression.invocationExpression(applied, [])
        | args -> (applied, args) ||> List.fold (fun e arg -> Expression.invocationExpression(e, [arg]))

    let transformCallAsStatements com ctx range (_: Fable.Type) returnStrategy callee callInfo =
        let argsLen (i: Fable.CallInfo) =
            List.length i.Args + (if Option.isSome i.ThisArg then 1 else 0)
        // Warn when there's a recursive call that couldn't be optimized?
        // TODO: Tail-call recursion, see transformCurriedApplyAsStatements too
        match returnStrategy, ctx.TailCallOpportunity with
        // | Some(Return|ReturnVoid), Some tc when tc.IsRecursiveRef(callee)
        //                                     && argsLen callInfo = List.length tc.Args ->
        //     let args =
        //         match callInfo.ThisArg with
        //         | Some thisArg -> thisArg::callInfo.Args
        //         | None -> callInfo.Args
        //     optimizeTailCall com ctx range tc args
        | _ ->
            [transformCall com ctx range callee callInfo |> resolveExpr returnStrategy]

    let transformCurriedApplyAsStatements com ctx range t returnStrategy callee args =
        // Warn when there's a recursive call that couldn't be optimized?
        match returnStrategy, ctx.TailCallOpportunity with
//        | Some(Return|ReturnUnit), Some tc when tc.IsRecursiveRef(callee)
//                                            && List.sameLength args tc.Args ->
//            optimizeTailCall com ctx range tc args
        | _ ->
            [transformCurriedApply com ctx range callee args |> resolveExpr returnStrategy]

    let transformCast (com: IDartCompiler) (ctx: Context) t e: Expression =
        match t with
        // Optimization for (numeric) array or list literals casted to seq
        // Done at the very end of the compile pipeline to get more opportunities
        // of matching cast and literal expressions after resolving pipes, inlining...
        | Fable.DeclaredType(ent,[_]) ->
            match ent.FullName with
            | Types.ienumerableGeneric | Types.ienumerable ->
                match e with
                | Replacements.Util.ArrayOrListLiteral(exprs, _) ->
                    exprs |> List.map (fun e -> com.TransformAsExpr(ctx, e)) |> Expression.listLiteral
                | _ -> com.TransformAsExpr(ctx, e)
            | _ -> com.TransformAsExpr(ctx, e)
        | Fable.Unit ->
            [com.TransformAsExpr(ctx, e)]
            |> libCall com ctx "Util" "ignore"
        | _ ->
            let e = com.TransformAsExpr(ctx, e)
            match transformType com ctx t with
            | Object -> e
            | t -> Expression.asExpression(e, t)

    // TODO: Try to identify type testing in the catch clause and use Dart's `on ...` exception checking
    let transformTryCatch com ctx r returnStrategy (body, catch, finalizer) =
        // try .. catch statements cannot be tail call optimized
        let ctx = { ctx with TailCallOpportunity = None }
        let handlers =
            catch |> Option.map (fun (param, body) ->
                let param = transformIdent com ctx param
                let body = com.TransformAsStatements(ctx, returnStrategy, body)
                CatchClause(param=param, body=body))
            |> Option.toList
        let finalizer =
            finalizer |> Option.map (transformAsStatements com ctx None)
        [Statement.tryStatement(transformAsStatements com ctx returnStrategy body,
            handlers=handlers, ?finalizer=finalizer)]

    let rec transformIfStatement (com: IDartCompiler) ctx r ret guardExpr thenStmnt elseStmnt =
        match com.TransformAsExpr(ctx, guardExpr) with
        | Literal(BooleanLiteral(value=value)) ->
            com.TransformAsStatements(ctx, ret, if value then thenStmnt else elseStmnt)
        | guardExpr ->
            let thenStmnt = com.TransformAsStatements(ctx, ret, thenStmnt)
            let elseStmnt = com.TransformAsStatements(ctx, ret, elseStmnt)
            [Statement.ifStatement(guardExpr, thenStmnt, elseStmnt)]

    let transformGet (com: IDartCompiler) ctx range typ fableExpr kind =
        match kind with
        | Fable.FieldGet(fieldName,_) ->
            let fableExpr =
                match fableExpr with
                // If we're accessing a virtual member with default implementation (see #701)
                // from base class, we can use `super` in JS so we don't need the bound this arg
                | Fable.Value(Fable.BaseValue(_,t), r) -> Fable.Value(Fable.BaseValue(None, t), r)
                | _ -> fableExpr
            let expr = com.TransformAsExpr(ctx, fableExpr)
            get expr fieldName

        | Fable.UnionTag ->
            com.TransformAsExpr(ctx, fableExpr) |> getUnionExprTag

        | Fable.UnionField(_caseIndex, fieldIndex) ->
            let expr = com.TransformAsExpr(ctx, fableExpr)
            let fields = getUnionExprFields expr
            let index = Expression.indexExpression(fields, Expression.integerLiteral fieldIndex)
            match typ with
            | Fable.Any -> index
            | typ -> Expression.asExpression(index, transformType com ctx typ)

        | e -> failwith $"todo: get ${e}"

    // TODO: tail calls, hoist vars
    let transformFunction com ctx name (args: Fable.Ident list) (body: Fable.Expr): Ident list * Statement list =
        let args = discardUnitArg args |> List.map (transformIdent com ctx)
        let ret = if body.Type = Fable.Unit then ReturnVoid else Return
        let body = transformAsStatements com ctx (Some ret) body
        args, body

    let transformSet (com: IDartCompiler) ctx range fableExpr (value: Fable.Expr) kind =
        let expr = com.TransformAsExpr(ctx, fableExpr)
        let value = com.TransformAsExpr(ctx, value)
        let ret =
            match kind with
            | Fable.ValueSet -> expr
            | Fable.ExprSet(TransformExpr com ctx e) -> getExpr expr e
            | Fable.FieldSet(fieldName) -> get expr fieldName
        assign range ret value

    let transformBindingExprBody (com: IDartCompiler) (ctx: Context) (var: Fable.Ident) (value: Fable.Expr) =
        match value with
        | Function(args, body) ->
            let args, body = transformFunction com ctx (Some var.Name) args body
            Expression.anonymousFunction(args, body)
        | _ ->
            if var.IsMutable then com.TransformAsExpr(ctx, value)
            else com.TransformAsExpr(ctx, value)

    let transformBindingAsExpr (com: IDartCompiler) ctx (var: Fable.Ident) (value: Fable.Expr) =
        transformBindingExprBody com ctx var value
        |> assign None (transformIdentAsExpr com ctx var)

    let transformBindingAsStatements (com: IDartCompiler) ctx (var: Fable.Ident) (value: Fable.Expr) =
        if isStatement ctx false value then
            let var, decl = transformLocalVarDeclaration com ctx var None
            let returnStrategy = Expression.identExpression var |> Assign |> Some
            decl :: com.TransformAsStatements(ctx, returnStrategy, value)
        else
            let _, decl =
                transformBindingExprBody com ctx var value
                |> Some
                |> transformLocalVarDeclaration com ctx var
            [decl]

    let transformSwitch (com: IDartCompiler) ctx returnStrategy evalExpr cases defaultCase: Statement =
        let cases =
            cases |> List.choose (fun (guards, expr) ->
                // Remove empty branches
                match returnStrategy, expr, guards with
                | None, Fable.Value(Fable.UnitConstant,_), _
                | _, _, [] -> None
                | _, _, guards ->
                    let guards = guards |> List.map (fun e -> com.TransformAsExpr(ctx, e))
                    let caseBody = com.TransformAsStatements(ctx, returnStrategy, expr)
                    SwitchCase(guards, caseBody) |> Some
                )
        let defaultCase =
            defaultCase
            |> Option.map (fun expr -> com.TransformAsStatements(ctx, returnStrategy, expr))
        Statement.switchStatement(com.TransformAsExpr(ctx, evalExpr), cases, defaultCase)

    let matchTargetIdentAndValues idents values =
        if List.isEmpty idents then []
        elif List.sameLength idents values then List.zip idents values
        else failwith "Target idents/values lengths differ"

    let getDecisionTargetAndBindValues (com: IDartCompiler) (ctx: Context) targetIndex boundValues =
        let idents, target = getDecisionTarget ctx targetIndex
        let identsAndValues = matchTargetIdentAndValues idents boundValues
        if not com.Options.DebugMode then
            let bindings, replacements =
                (([], Map.empty), identsAndValues)
                ||> List.fold (fun (bindings, replacements) (ident, expr) ->
                    if canHaveSideEffects expr then
                        (ident, expr)::bindings, replacements
                    else
                        bindings, Map.add ident.Name expr replacements)
            let target = FableTransforms.replaceValues replacements target
            List.rev bindings, target
        else
            identsAndValues, target

    let transformDecisionTreeSuccessAsExpr (com: IDartCompiler) (ctx: Context) targetIndex boundValues =
        let bindings, target = getDecisionTargetAndBindValues com ctx targetIndex boundValues
        match bindings with
        | [] -> com.TransformAsExpr(ctx, target)
        | bindings ->
            let target = List.rev bindings |> List.fold (fun e (i,v) -> Fable.Let(i,v,e)) target
            com.TransformAsExpr(ctx, target)

    let transformDecisionTreeSuccessAsStatements (com: IDartCompiler) (ctx: Context) returnStrategy targetIndex boundValues: Statement list =
        match returnStrategy with
        | Some(Target targetId) ->
            let idents, _ = getDecisionTarget ctx targetIndex
            let assignments =
                matchTargetIdentAndValues idents boundValues
                |> List.map (fun (id, TransformExpr com ctx value) ->
                    assign None (transformIdentAsExpr com ctx id) value
                    |> ExpressionStatement)
            let targetAssignment =
                assign None (IdentExpression targetId) (Expression.integerLiteral targetIndex)
                |> ExpressionStatement
            List.append [targetAssignment] assignments
        | ret ->
            let bindings, target = getDecisionTargetAndBindValues com ctx targetIndex boundValues
            let bindings = bindings |> List.collect (fun (i, v) -> transformBindingAsStatements com ctx i v)
            List.append bindings (com.TransformAsStatements(ctx, ret, target))

    let transformDecisionTreeAsSwitch expr =
        let (|Equals|_|) = function
            | Fable.Operation(Fable.Binary(BinaryEqual, expr, right), _, _) ->
                match expr with
                | Fable.Value((Fable.CharConstant _ | Fable.StringConstant _ | Fable.NumberConstant _), _) -> Some(expr, right)
                | _ -> None
            | Fable.Test(expr, Fable.UnionCaseTest tag, _) ->
                let evalExpr = Fable.Get(expr, Fable.UnionTag, numType Int32, None)
                let right = makeIntConst tag
                Some(evalExpr, right)
            | _ -> None
        let sameEvalExprs evalExpr1 evalExpr2 =
            match evalExpr1, evalExpr2 with
            | Fable.IdentExpr i1, Fable.IdentExpr i2
            | Fable.Get(Fable.IdentExpr i1,Fable.UnionTag,_,_), Fable.Get(Fable.IdentExpr i2,Fable.UnionTag,_,_) ->
                i1.Name = i2.Name
            | Fable.Get(Fable.IdentExpr i1, Fable.FieldGet(fieldName1, _),_,_), Fable.Get(Fable.IdentExpr i2, Fable.FieldGet(fieldName2, _),_,_) ->
                i1.Name = i2.Name && fieldName1 = fieldName2
            | _ -> false
        let rec checkInner cases evalExpr = function
            | Fable.IfThenElse(Equals(evalExpr2, caseExpr),
                               Fable.DecisionTreeSuccess(targetIndex, boundValues, _), treeExpr, _)
                                    when sameEvalExprs evalExpr evalExpr2 ->
                match treeExpr with
                | Fable.DecisionTreeSuccess(defaultTargetIndex, defaultBoundValues, _) ->
                    let cases = (caseExpr, targetIndex, boundValues)::cases |> List.rev
                    Some(evalExpr, cases, (defaultTargetIndex, defaultBoundValues))
                | treeExpr -> checkInner ((caseExpr, targetIndex, boundValues)::cases) evalExpr treeExpr
            | _ -> None
        match expr with
        | Fable.IfThenElse(Equals(evalExpr, caseExpr),
                           Fable.DecisionTreeSuccess(targetIndex, boundValues, _), treeExpr, _) ->
            match checkInner [caseExpr, targetIndex, boundValues] evalExpr treeExpr with
            | Some(evalExpr, cases, defaultCase) ->
                Some(evalExpr, cases, defaultCase)
            | None -> None
        | _ -> None

    let transformDecisionTreeAsExpr (com: IDartCompiler) (ctx: Context) targets expr: Expression =
        // TODO: Check if some targets are referenced multiple times
        let ctx = { ctx with DecisionTargets = targets }
        com.TransformAsExpr(ctx, expr)

    let groupSwitchCases t (cases: (Fable.Expr * int * Fable.Expr list) list) (defaultIndex, defaultBoundValues) =
        cases
        |> List.groupBy (fun (_,idx,boundValues) ->
            // Try to group cases with some target index and empty bound values
            // If bound values are non-empty use also a non-empty Guid to prevent grouping
            if List.isEmpty boundValues
            then idx, System.Guid.Empty
            else idx, System.Guid.NewGuid())
        |> List.map (fun ((idx,_), cases) ->
            let caseExprs = cases |> List.map Tuple3.item1
            // If there are multiple cases, it means boundValues are empty
            // (see `groupBy` above), so it doesn't mind which one we take as reference
            let boundValues = cases |> List.head |> Tuple3.item3
            caseExprs, Fable.DecisionTreeSuccess(idx, boundValues, t))
        |> function
            | [] -> []
            // Check if the last case can also be grouped with the default branch, see #2357
            | cases when List.isEmpty defaultBoundValues ->
                match List.splitLast cases with
                | cases, (_, Fable.DecisionTreeSuccess(idx, [], _))
                    when idx = defaultIndex -> cases
                | _ -> cases
            | cases -> cases

    let getTargetsWithMultipleReferences expr =
        let rec findSuccess (targetRefs: Map<int,int>) = function
            | [] -> targetRefs
            | expr::exprs ->
                match expr with
                // We shouldn't actually see this, but shortcircuit just in case
                | Fable.DecisionTree _ ->
                    findSuccess targetRefs exprs
                | Fable.DecisionTreeSuccess(idx,_,_) ->
                    let count =
                        Map.tryFind idx targetRefs
                        |> Option.defaultValue 0
                    let targetRefs = Map.add idx (count + 1) targetRefs
                    findSuccess targetRefs exprs
                | expr ->
                    let exprs2 = FableTransforms.getSubExpressions expr
                    findSuccess targetRefs (exprs @ exprs2)
        findSuccess Map.empty [expr] |> Seq.choose (fun kv ->
            if kv.Value > 1 then Some kv.Key else None) |> Seq.toList

    /// When several branches share target create first a switch to get the target index and bind value
    /// and another to execute the actual target
    let transformDecisionTreeWithTwoSwitches (com: IDartCompiler) ctx returnStrategy
                    (targets: (Fable.Ident list * Fable.Expr) list) treeExpr =
        // Declare target and bound idents
        let targetId =
            getUniqueNameInDeclarationScope ctx "pattern_matching_result"
            |> makeTypedIdent (numType Int32)
        let varDecls =
            [
                transformIdent com ctx targetId
                yield! targets |> List.collect (fun (idents,_) ->
                    idents |> List.map (transformIdent com ctx))
            ]
            |> List.map Statement.variableDeclaration
        // Transform targets as switch
        let switch2 =
            // TODO: Declare the last case as the default case?
            let cases = targets |> List.mapi (fun i (_,target) -> [makeIntConst i], target)
            transformSwitch com ctx returnStrategy (targetId |> Fable.IdentExpr) cases None
        // Transform decision tree
        let targetAssign = Target(transformIdent com ctx targetId)
        let ctx = { ctx with DecisionTargets = targets }
        match transformDecisionTreeAsSwitch treeExpr with
        | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
            let cases = groupSwitchCases (numType Int32) cases (defaultIndex, defaultBoundValues)
            let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, numType Int32)
            let switch1 = transformSwitch com ctx (Some targetAssign) evalExpr cases (Some defaultCase)
            varDecls @ [switch1; switch2]
        | None ->
            let decisionTree = com.TransformAsStatements(ctx, Some targetAssign, treeExpr)
            varDecls @ decisionTree @ [switch2]

    let transformDecisionTreeAsStatements (com: IDartCompiler) (ctx: Context) returnStrategy
                        (targets: (Fable.Ident list * Fable.Expr) list) (treeExpr: Fable.Expr): Statement list =
        // If some targets are referenced multiple times, hoist bound idents,
        // resolve the decision index and compile the targets as a switch
        let targetsWithMultiRefs =
            if com.Options.Language = TypeScript then [] // no hoisting when compiled with types
            else getTargetsWithMultipleReferences treeExpr
        match targetsWithMultiRefs with
        | [] ->
            let ctx = { ctx with DecisionTargets = targets }
            match transformDecisionTreeAsSwitch treeExpr with
            | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
                let t = treeExpr.Type
                let cases = cases |> List.map (fun (caseExpr, targetIndex, boundValues) ->
                    [caseExpr], Fable.DecisionTreeSuccess(targetIndex, boundValues, t))
                let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, t)
                [transformSwitch com ctx returnStrategy evalExpr cases (Some defaultCase)]
            | None ->
                com.TransformAsStatements(ctx, returnStrategy, treeExpr)
        | targetsWithMultiRefs ->
            // If the bound idents are not referenced in the target, remove them
            let targets =
                targets |> List.map (fun (idents, expr) ->
                    idents
                    |> List.exists (fun i -> FableTransforms.isIdentUsed i.Name expr)
                    |> function
                        | true -> idents, expr
                        | false -> [], expr)
            let hasAnyTargetWithMultiRefsBoundValues =
                targetsWithMultiRefs |> List.exists (fun idx ->
                    targets.[idx] |> fst |> List.isEmpty |> not)
            if not hasAnyTargetWithMultiRefsBoundValues then
                match transformDecisionTreeAsSwitch treeExpr with
                | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
                    let t = treeExpr.Type
                    let cases = groupSwitchCases t cases (defaultIndex, defaultBoundValues)
                    let ctx = { ctx with DecisionTargets = targets }
                    let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, t)
                    [transformSwitch com ctx returnStrategy evalExpr cases (Some defaultCase)]
                | None ->
                    transformDecisionTreeWithTwoSwitches com ctx returnStrategy targets treeExpr
            else
                transformDecisionTreeWithTwoSwitches com ctx returnStrategy targets treeExpr

    let transformTest (com: IDartCompiler) ctx range kind expr: Expression =
        let expr = com.TransformAsExpr(ctx, expr)
        match kind with
        | Fable.TypeTest t ->
            Expression.isExpression(expr, transformType com ctx t)
        | Fable.OptionTest isSome -> failwith "todo: option test"
        | Fable.ListTest nonEmpty -> failwith "todo: list test"
        | Fable.UnionCaseTest tag ->
            let expected = Expression.integerLiteral tag
            let actual = getUnionExprTag expr
            Expression.binaryExpression(BinaryEqual, actual, expected)

    let transformObjectExpr (com: IDartCompiler) ctx r (members: Fable.MemberDecl list) baseCall: Expression =
        addErrorAndReturnNull com r "TODO: object expression"

    let rec transformAsExpr (com: IDartCompiler) ctx (expr: Fable.Expr): Expression =
        match expr with
        | Fable.Unresolved(_,_,r) -> addErrorAndReturnNull com r "Unexpected unresolved expression"

        | Fable.TypeCast(e, t) -> transformCast com ctx t e

        | Fable.Value(kind, r) -> transformValue com ctx r kind

        | Fable.IdentExpr ident -> transformIdentAsExpr com ctx ident

        | Fable.Import({ Selector = selector; Path = path }, _, r) ->
            transformImport com ctx r selector path

        | Fable.Test(expr, kind, range) ->
            transformTest com ctx range kind expr

        | Fable.Lambda(arg, body, info) ->
            let args, body = transformFunction com ctx info.Name [arg] body
            Expression.anonymousFunction(args, body)

        | Fable.Delegate(args, body, info) ->
            let args, body = transformFunction com ctx info.Name args body
            Expression.anonymousFunction(args, body)

        | Fable.ObjectExpr (members, _, baseCall) ->
           transformObjectExpr com ctx expr.Range members baseCall

        | Fable.Call(callee, info, _, range) ->
            transformCall com ctx range callee info

        | Fable.CurriedApply(callee, args, _, range) ->
            transformCurriedApply com ctx range callee args

        | Fable.Operation(kind, t, r) -> transformOperation com ctx r t kind

        | Fable.Get(expr, kind, typ, range) ->
            transformGet com ctx range typ expr kind

        | Fable.IfThenElse(TransformExpr com ctx guardExpr,
                           TransformExpr com ctx thenExpr,
                           TransformExpr com ctx elseExpr, r) ->
            Expression.conditionalExpression(guardExpr, thenExpr, elseExpr)

        | Fable.DecisionTree(expr, targets) ->
            transformDecisionTreeAsExpr com ctx targets expr

        | Fable.DecisionTreeSuccess(idx, boundValues, _) ->
            transformDecisionTreeSuccessAsExpr com ctx idx boundValues

        | Fable.Set(expr, kind, _typ, value, range) ->
            transformSet com ctx range expr value kind

        | Fable.Let(ident, value, body) ->
            if ctx.HoistVars [ident] then
                let assignment = transformBindingAsExpr com ctx ident value
                com.TransformAsExpr(ctx, body)
                |> sequenceExpression com ctx [assignment]
            else iife com ctx expr

        | Fable.LetRec(bindings, body) ->
            if ctx.HoistVars(List.map fst bindings) then
                let assignments = bindings |> List.map (fun (id, value) ->
                    transformBindingAsExpr com ctx id value)
                com.TransformAsExpr(ctx, body)
                |> sequenceExpression com ctx assignments
            else iife com ctx expr

        | Fable.Emit(info, _, range) ->
            if info.IsStatement then iife com ctx expr
            else transformEmit com ctx range info

        | Fable.Sequential(exprs) ->
            exprs
            |> List.map (transformAsExpr com ctx)
            |> List.splitLast
            ||> sequenceExpression com ctx

        // These cannot appear in expression position in JS, must be wrapped in a lambda
        | Fable.WhileLoop _ | Fable.ForLoop _ | Fable.TryCatch _ -> iife com ctx expr

        | Fable.Extended(instruction, _) ->
            match instruction with
            | Fable.Throw(None, _) -> Expression.rethrowExpression()
            | Fable.Throw(Some(TransformExpr com ctx e), _) -> Expression.throwExpression(e)
            | Fable.Debugger -> extLibCall com ctx "dart:developer" "debugger" []
            | Fable.Curry(e, arity) -> failwith "todo: transformCurry (expr)"
            | Fable.RegionStart _ -> iife com ctx expr

    let rec transformAsStatements (com: IDartCompiler) ctx (returnStrategy: ReturnStrategy option) (expr: Fable.Expr): Statement list =
        match expr with
        | Fable.Unresolved(_,_,r) ->
            addError com [] r "Unexpected unresolved expression"
            []

        | Fable.Extended(kind, r) ->
            match kind with
            | Fable.Throw(None, _) ->
                [Expression.rethrowExpression() |> Statement.ExpressionStatement]
            | Fable.Throw(Some(TransformExpr com ctx e), _) ->
                [Expression.throwExpression(e) |> Statement.ExpressionStatement]
            | Fable.Debugger -> [extLibCall com ctx "dart:developer" "debugger" [] |> Statement.ExpressionStatement]
            | Fable.Curry(e, arity) -> failwith "todo: transformCurry (statement)"
            | Fable.RegionStart _ -> []

        | Fable.TypeCast(e, t) ->
            [transformCast com ctx t e |> resolveExpr returnStrategy]

        | Fable.Value(kind, r) ->
            match kind with
            | Fable.UnitConstant -> []
            | kind -> [transformValue com ctx r kind |> resolveExpr returnStrategy]

        | Fable.IdentExpr id ->
            [transformIdentAsExpr com ctx id |> resolveExpr returnStrategy]

        | Fable.Import({ Selector = selector; Path = path }, _t, r) ->
            [transformImport com ctx r selector path |> resolveExpr returnStrategy]

        | Fable.Test(expr, kind, range) ->
            [transformTest com ctx range kind expr |> resolveExpr returnStrategy]

        | Fable.Lambda(arg, body, info) ->
            let args, body = transformFunction com ctx info.Name [arg] body
            Expression.anonymousFunction(args, body)
            |> resolveExpr returnStrategy
            |> List.singleton

        | Fable.Delegate(args, body, info) ->
            let args, body = transformFunction com ctx info.Name args body
            Expression.anonymousFunction(args, body)
            |> resolveExpr returnStrategy
            |> List.singleton

        | Fable.ObjectExpr (members, _, baseCall) ->
            [transformObjectExpr com ctx expr.Range members baseCall |> resolveExpr returnStrategy]

        | Fable.Call(callee, info, typ, range) ->
            transformCallAsStatements com ctx range typ returnStrategy callee info

        | Fable.CurriedApply(callee, args, typ, range) ->
            transformCurriedApplyAsStatements com ctx range typ returnStrategy callee args

        | Fable.Emit(info, t, range) ->
            let e = transformEmit com ctx range info
            if info.IsStatement then [ExpressionStatement(e)] // Ignore the return strategy
            else [resolveExpr returnStrategy e]

        | Fable.Operation(kind, t, r) ->
            [transformOperation com ctx r t kind |> resolveExpr returnStrategy]

        | Fable.Get(expr, kind, t, range) ->
            [transformGet com ctx range t expr kind |> resolveExpr returnStrategy]

        | Fable.Let(ident, value, body) ->
            let binding = transformBindingAsStatements com ctx ident value
            List.append binding (transformAsStatements com ctx returnStrategy body)

        | Fable.LetRec(bindings, body) ->
            let bindings = bindings |> List.collect (fun (i, v) -> transformBindingAsStatements com ctx i v)
            List.append bindings (transformAsStatements com ctx returnStrategy body)

        | Fable.Set(expr, kind, typ, value, range) ->
            [transformSet com ctx range expr value kind |> resolveExpr returnStrategy]

        | Fable.IfThenElse(guardExpr, thenExpr, elseExpr, r) ->
            let asStatement =
                match returnStrategy with
                | None | Some ReturnVoid -> true
                | Some(Target _) -> true // Compile as statement so values can be bound
                | Some(Assign _) -> (isStatement ctx false thenExpr) || (isStatement ctx false elseExpr)
                | Some Return ->
                    Option.isSome ctx.TailCallOpportunity
                    || (isStatement ctx false thenExpr) || (isStatement ctx false elseExpr)
            if asStatement then
                transformIfStatement com ctx r returnStrategy guardExpr thenExpr elseExpr
            else
                let guardExpr' = transformAsExpr com ctx guardExpr
                let thenExpr' = transformAsExpr com ctx thenExpr
                let elseExpr' = transformAsExpr com ctx elseExpr
                [Expression.conditionalExpression(guardExpr', thenExpr', elseExpr') |> resolveExpr returnStrategy]

        | Fable.Sequential statements ->
            let lasti = (List.length statements) - 1
            statements |> List.mapi (fun i statement ->
                let ret = if i < lasti then None else returnStrategy
                transformAsStatements com ctx ret statement)
            |> List.concat

        | Fable.TryCatch (body, catch, finalizer, r) ->
            transformTryCatch com ctx r returnStrategy (body, catch, finalizer)

        | Fable.DecisionTree(expr, targets) ->
            transformDecisionTreeAsStatements com ctx returnStrategy targets expr

        | Fable.DecisionTreeSuccess(idx, boundValues, _) ->
            transformDecisionTreeSuccessAsStatements com ctx returnStrategy idx boundValues

        | Fable.WhileLoop(TransformExpr com ctx guard, body, label, range) ->
            let whileLoop = Statement.whileStatement(guard, transformAsStatements com ctx None body)
            match label with
            | Some label -> [Statement.labeledStatement(label, whileLoop)]
            | None -> [whileLoop]

        | Fable.ForLoop (var, TransformExpr com ctx start, TransformExpr com ctx limit, body, isUp, range) ->
            let op1, op2 =
                if isUp
                then BinaryOperator.BinaryLessOrEqual, UpdateOperator.UpdatePlus
                else BinaryOperator.BinaryGreaterOrEqual, UpdateOperator.UpdateMinus

            let param = transformIdent com ctx var
            let paramExpr = Expression.identExpression param
            [Statement.forStatement(
                transformAsStatements com ctx None body, (param, start),
                Expression.binaryExpression(op1, paramExpr, limit),
                Expression.updateExpression(op2, paramExpr)
            )]

    let getMemberArgsAndBody (com: IDartCompiler) ctx kind (args: Fable.Ident list) (body: Fable.Expr) =
        let funcName, args, body =
            match kind, args with
            | Attached(isStatic=false), (thisArg::args) ->
                let body =
                    // TODO: If ident is not captured maybe we can just replace it with "this"
                    if FableTransforms.isIdentUsed thisArg.Name body then
                        let thisKeyword = Fable.IdentExpr { thisArg with Name = "this" }
                        Fable.Let(thisArg, thisKeyword, body)
                    else body
                None, args, body
            | Attached(isStatic=true), _
            | ClassConstructor, _ -> None, args, body
            | NonAttached funcName, _ -> Some funcName, args, body
            | _ -> None, args, body

        transformFunction com ctx funcName args body

    let transformModuleFunction (com: IDartCompiler) ctx (memb: Fable.MemberDecl) =
        let returnType = transformType com ctx memb.Body.Type
        let args, body = getMemberArgsAndBody com ctx (NonAttached memb.Name) memb.ArgIdents memb.Body
        let isEntryPoint =
            memb.Info.Attributes
            |> Seq.exists (fun att -> att.Entity.FullName = Atts.entryPoint)
        if isEntryPoint then
            failwith "todo: main function"
        else
            let argTypes = memb.ArgIdents |> List.map (fun a -> a.Type)
            let rec getGenParams = function
                | Fable.GenericParam(name, _constraints) -> [name]
                | t -> t.Generics |> List.collect getGenParams
            let genParams =
                (Set.empty, memb.Body.Type::argTypes) ||> List.fold (fun genParams t ->
                    (genParams, getGenParams t) ||> List.fold (fun genParams n -> Set.add n genParams))
            Declaration.functionDeclaration(memb.Name, args, body, returnType, genParams=List.ofSeq genParams)

    let transformInterfaceDeclaration (com: IDartCompiler) ctx (decl: Fable.ClassDecl) (ent: Fable.Entity) =
        let methods =
            ent.MembersFunctionsAndValues
            |> Seq.choose (fun m ->
                // TODO: Indexed properties
                if m.IsGetter then Some IsGetter
                elif m.IsSetter then Some IsSetter
                elif m.IsProperty then None
                else Some IsMethod
                |> Option.map (fun kind ->
                    let name = m.DisplayName
                    let args =
                        m.CurriedParameterGroups
                        |> List.concat
                        |> List.mapi (fun i p ->
                            let name =
                                match p.Name with
                                | Some name -> name
                                | None -> $"$arg{i}"
                            let t = transformType com ctx p.Type
                            makeIdent t name)
                    // TODO: genParams
                    InstanceMethod(name, kind=kind, args=args, returnType=transformType com ctx m.ReturnParameter.Type)
                )
            )
            |> Seq.toList
        [Declaration.classDeclaration(decl.Name, methods=methods, isAbstract=true)]

    let transformUnionDeclaration (com: IDartCompiler) ctx (decl: Fable.ClassDecl) (ent: Fable.Entity) =
        let extends = libTypeRef com ctx "Types" "Union" []
        let selfTypeRef = makeTypeRefFromName decl.Name []
        let implements = makeTypeRefFromName "Comparable" [selfTypeRef]
        let cons =
            let tag = makeIdent Integer "tag"
            let fields = makeIdent (Type.List Object) "fields"
            Constructor(args=[ConsArg tag; ConsArg fields], superArgs=[tag; fields], isConst=true)
        let compareTo =
            let other = makeIdent selfTypeRef "other"
            let body =
                Expression.invocationExpression(SuperExpression, "compareTagAndFields", [Expression.identExpression other])
                |> makeReturnBlock
            InstanceMethod("compareTo", [other], Integer, body=body, isOverride=true)
        [Declaration.classDeclaration(
            decl.Name,
            constructor=cons,
            extends=extends,
            implements=[implements],
            methods=[compareTo])]

    // TODO: generic args
    let transformRecordDeclaration (com: IDartCompiler) ctx (decl: Fable.ClassDecl) (ent: Fable.Entity) =
        let selfTypeRef = makeTypeRefFromName decl.Name []
        let implements = [
            libTypeRef com ctx "Types" "Record" []
            makeTypeRefFromName "Comparable" [selfTypeRef]
        ]
        let mutable hasMutableFields = false
        let fields, varDecls, consArgs =
            ent.FSharpFields
            |> List.map (fun f ->
                let kind =
                    if f.IsMutable then
                        hasMutableFields <- true
                        Var
                    else
                        Final
                let ident = transformIdentWith com ctx f.FieldType f.Name
                ident, InstanceVariable(ident, kind=kind), ConsThisArg f.Name)
            |> List.unzip3

        let cons = Constructor(args=consArgs, isConst=not hasMutableFields)

        let equals =
            let other = makeIdent Object "other"

            let makeFieldEq (field: Ident) =
                let otherField = { field with Prefix = Some other.Name }
                Expression.binaryExpression(BinaryEqual, otherField.Expr, field.Expr)

            let rec makeFieldsEq fields acc =
                match fields with
                | [] -> acc
                | field::fields ->
                    let eq = makeFieldEq field
                    Expression.logicalExpression(LogicalAnd, eq, acc)
                    |> makeFieldsEq fields

            let typeTest =
                Expression.isExpression(other.Expr, selfTypeRef)

            let body =
                match List.rev fields with
                | [] -> typeTest
                | field::fields ->
                    let eq = makeFieldEq field |> makeFieldsEq fields
                    Expression.logicalExpression(LogicalAnd, typeTest, eq)
                |> makeReturnBlock

            InstanceMethod("operator ==", [other], Boolean, body=body, isOverride=true)

        let hashCode =
            let body =
                fields
                |> List.map (fun f -> Expression.propertyAccess(Expression.identExpression f, "hashCode"))
                |> fun hashCodes -> [Expression.listLiteral(hashCodes)]
                |> libCall com ctx "Util" "combineHashCodes"
                |> makeReturnBlock
            InstanceMethod("hashCode", [], Integer, kind=IsGetter, body=body, isOverride=true)

        let compareTo =
            let r = makeIdent Integer "$r"
            let other = makeIdent selfTypeRef "other"

            let makeAssign (field: Ident) =
                let otherField = { field with Prefix = Some other.Name }
                Expression.assignmentExpression(r.Expr,
                    Expression.invocationExpression(field.Expr, "compareTo", [otherField.Expr]))

            let makeFieldComp (field: Ident) =
                Expression.binaryExpression(BinaryEqual, makeAssign field, Expression.integerLiteral 0)

            let rec makeFieldsComp (fields: Ident list) (acc: Statement list) =
                match fields with
                | [] -> acc
                | field::fields ->
                    let eq = makeFieldComp field
                    [Statement.ifStatement(eq, acc)]
                    |> makeFieldsComp fields

            let body = [
                Statement.variableDeclaration(r, kind=Var)
                yield!
                    match List.rev fields with
                    | [] -> []
                    | field::fields ->
                        [makeAssign field |> ExpressionStatement]
                        |> makeFieldsComp fields
                Statement.returnStatement r.Expr
            ]

            InstanceMethod("compareTo", [other], Integer, body=body, isOverride=true)

        [Declaration.classDeclaration(
            decl.Name,
            constructor=cons,
            implements=implements,
            variables=varDecls,
            methods=[equals; hashCode; compareTo])]

    let transformClassDeclaration (com: IDartCompiler) ctx (decl: Fable.ClassDecl) =
        let entRef = decl.Entity
        let ent = com.GetEntity(entRef)
        // TODO: Custom members
        // TODO: Implementing interfaces
        if ent.IsInterface then
            transformInterfaceDeclaration com ctx decl ent
        elif ent.IsFSharpUnion then
            transformUnionDeclaration com ctx decl ent
        elif ent.IsFSharpRecord then
            transformRecordDeclaration com ctx decl ent
        else
            let cons = Constructor()
            [Declaration.classDeclaration(decl.Name, constructor=cons)]

    let rec transformDeclaration (com: IDartCompiler) ctx decl =
        let withCurrentScope ctx (usedNames: Set<string>) f =
            let ctx = { ctx with UsedNames = { ctx.UsedNames with CurrentDeclarationScope = HashSet usedNames } }
            let result = f ctx
            ctx.UsedNames.DeclarationScopes.UnionWith(ctx.UsedNames.CurrentDeclarationScope)
            result

        match decl with
        | Fable.ModuleDeclaration decl ->
            decl.Members |> List.collect (transformDeclaration com ctx)

        | Fable.MemberDeclaration memb ->
            withCurrentScope ctx memb.UsedNames <| fun ctx ->
                if memb.Info.IsValue then
                    // TODO: Prefix non-public values with underscore or raise warning?
                    let ident = transformIdentWith com ctx memb.Body.Type memb.Name
                    // TODO: If value is primitive, list, union or record without mutable fields
                    // we can declare it as const (if var is mutable we can make only the value const)
                    let kind = if memb.Info.IsMutable then Var else Final
                    let value = transformAsExpr com ctx memb.Body
                    [Declaration.variableDeclaration(ident, kind, value)]
                else
                    [transformModuleFunction com ctx memb]

        // TODO: Action declarations are not supported in Dart, compile as: var _ = ...
        | Fable.ActionDeclaration _ -> []
//            withCurrentScope ctx decl.UsedNames <| fun ctx ->
//                transformAction com ctx decl.Body

        | Fable.ClassDeclaration decl ->
            transformClassDeclaration com ctx decl

    let getIdentForImport (ctx: Context) (path: string) =
        Path.GetFileNameWithoutExtension(path).Replace(".", "_")
        |> Naming.applyCaseRule Core.CaseRules.SnakeCase
        |> getUniqueNameInRootScope ctx

module Compiler =
    open Util

    type DartCompiler (com: Compiler) =
        let onlyOnceWarnings = HashSet<string>()
        let imports = Dictionary<string, Import>()

        interface IDartCompiler with
            member _.WarnOnlyOnce(msg, ?range) =
                if onlyOnceWarnings.Add(msg) then
                    addWarning com [] range msg

            member _.GetImportIdent(ctx, selector, path, r) =
                let localId =
                    match imports.TryGetValue(path) with
                    | true, i ->
                        match i.LocalIdent with
                        | Some localId -> localId
                        | None ->
                            let localId = getIdentForImport ctx path
                            imports[path] <- { Path = path; LocalIdent = Some localId }
                            localId
                    | false, _ ->
                        let localId = getIdentForImport ctx path
                        imports.Add(path, { Path = path; LocalIdent = Some localId })
                        localId
                let ident = makeIdent Object localId
                match selector with
                | Naming.placeholder ->
                    "`importMember` must be assigned to a variable"
                    |> addError com [] r
                    ident
                | "*" -> ident
                | selector -> { ident with Prefix = Some ident.Name; Name = selector }

            member _.GetAllImports() = imports.Values |> Seq.toList
            member this.TransformType(ctx, t) = transformType this ctx t
            member this.TransformAsExpr(ctx, e) = transformAsExpr this ctx e
            member this.TransformAsStatements(ctx, ret, e) = transformAsStatements this ctx ret e
            member this.TransformFunction(ctx, name, args, body) = transformFunction this ctx name args body

        interface Compiler with
            member _.Options = com.Options
            member _.Plugins = com.Plugins
            member _.LibraryDir = com.LibraryDir
            member _.CurrentFile = com.CurrentFile
            member _.OutputDir = com.OutputDir
            member _.OutputType = com.OutputType
            member _.ProjectFile = com.ProjectFile
            member _.IsPrecompilingInlineFunction = com.IsPrecompilingInlineFunction
            member _.WillPrecompileInlineFunction(file) = com.WillPrecompileInlineFunction(file)
            member _.GetImplementationFile(fileName) = com.GetImplementationFile(fileName)
            member _.GetRootModule(fileName) = com.GetRootModule(fileName)
            member _.TryGetEntity(fullName) = com.TryGetEntity(fullName)
            member _.GetInlineExpr(fullName) = com.GetInlineExpr(fullName)
            member _.AddWatchDependency(fileName) = com.AddWatchDependency(fileName)
            member _.AddLog(msg, severity, ?range, ?fileName:string, ?tag: string) =
                com.AddLog(msg, severity, ?range=range, ?fileName=fileName, ?tag=tag)

    let makeCompiler com = DartCompiler(com)

    let transformFile (com: Compiler) (file: Fable.File) =
        let com = makeCompiler com :> IDartCompiler
        let declScopes =
            let hs = HashSet()
            for decl in file.Declarations do
                hs.UnionWith(decl.UsedNames)
            hs

        let ctx =
          { File = file
            UsedNames = { RootScope = HashSet file.UsedNamesInRootScope
                          DeclarationScopes = declScopes
                          CurrentDeclarationScope = Unchecked.defaultof<_> }
            DecisionTargets = []
            HoistVars = fun _ -> false
            TailCallOpportunity = None
            OptimizeTailCall = fun () -> () }
        let rootDecls = List.collect (transformDeclaration com ctx) file.Declarations
        let imports = com.GetAllImports()
        { File.Imports = imports
          Declarations = rootDecls }
