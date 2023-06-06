module Telplin.Core.UntypedTree.TypeForValNode

open Fantomas.FCS.Text
open Fantomas.Core.SyntaxOak
open Microsoft.FSharp.Core.CompilerServices
open Telplin.Core
open Telplin.Core.UntypedTree.SourceParser
open Telplin.Core.UntypedTree.ASTCreation

let mapTypeWithGlobalConstraintsNode (map : Type -> Type) (t : Type) =
    match t with
    | Type.WithGlobalConstraints globalConstraintsNode ->
        TypeWithGlobalConstraintsNode (
            map globalConstraintsNode.Type,
            globalConstraintsNode.TypeConstraints,
            globalConstraintsNode.Range
        )
        |> Type.WithGlobalConstraints
    | _ -> map t

/// Typed tree information for a `FSharpMemberOrFunctionOrValue`
type TypedTreeInfo =
    {
        ReturnType : Type
        BindingGenericParameters : GenericParameter list
        TypeGenericParameters : GenericParameter list
    }

let (|WildCardArray|_|) (t : Type) =
    match t with
    | Type.Array arrayNode ->
        match arrayNode.Type with
        | Type.Anon _ -> Some arrayNode
        | _ -> None
    | _ -> None

let rec updateTypeBasedOnUnTyped (typedTreeType : Type) (untypedTreeType : Type) : Type =
    // Sometimes the type in the untyped tree is more accurate than what the typed tree returned.
    // For example `System.Text.RegularExpressions.Regex` by untyped tree versus `Regex` by typed.
    match untypedTreeType, typedTreeType with
    // Don't take a wildcard
    | (WildCardArray _ | Type.Anon _), typedTreeType -> typedTreeType
    // The typed tree will have any constraints at the end as part of a Type.WithGlobalConstraints
    | Type.WithSubTypeConstraint _, typedTreeType
    | TParen (Type.WithSubTypeConstraint _), typedTreeType -> typedTreeType
    | Type.Funs _ as untypedFuns, TParen (Type.Funs _ as typedFuns) ->
        updateTypeBasedOnUnTyped typedFuns untypedFuns |> wrapTypeInParentheses
    | Type.Funs untypedFuns, Type.Funs typedFuns when untypedFuns.Parameters.Length = typedFuns.Parameters.Length ->
        let parameters =
            (untypedFuns.Parameters, typedFuns.Parameters)
            ||> List.zip
            |> List.map (fun ((ut, _), (tt, arrow)) -> updateTypeBasedOnUnTyped tt ut, arrow)

        let returnType =
            updateTypeBasedOnUnTyped typedFuns.ReturnType untypedFuns.ReturnType

        TypeFunsNode (parameters, returnType, untypedFuns.Range) |> Type.Funs
    // An optional parameter that is a function type.
    | Type.Funs _, Type.AppPostfix _ -> wrapTypeInParentheses untypedTreeType
    | Type.AppPrefix untypedAppPrefix, Type.AppPrefix typedAppPrefix when
        (untypedAppPrefix.Arguments.Length = typedAppPrefix.Arguments.Length)
        ->
        let arguments =
            (untypedAppPrefix.Arguments, typedAppPrefix.Arguments)
            ||> List.zip
            |> List.map (fun (ut, tt) -> updateTypeBasedOnUnTyped tt ut)

        TypeAppPrefixNode (
            typedAppPrefix.Identifier,
            typedAppPrefix.PostIdentifier,
            typedAppPrefix.LessThen,
            arguments,
            typedAppPrefix.GreaterThan,
            typedAppPrefix.Range
        )
        |> Type.AppPrefix
    | Type.AppPostfix untypedAppPostFix, Type.AppPostfix typedAppPostFix ->
        TypeAppPostFixNode (
            updateTypeBasedOnUnTyped typedAppPostFix.First untypedAppPostFix.First,
            updateTypeBasedOnUnTyped typedAppPostFix.Last typedAppPostFix.Last,
            untypedAppPostFix.Range
        )
        |> Type.AppPostfix
    // A rather specific fix for https://github.com/nojaf/telplin/issues/47
    | Type.HashConstraint untypedHashConstraint, _ ->
        match untypedHashConstraint.Type with
        | Type.AppPrefix untypedAppPrefix ->
            let hasWildcard =
                untypedAppPrefix.Arguments
                |> List.exists (
                    function
                    | Type.Anon _ -> true
                    | _ -> false
                )

            if hasWildcard then typedTreeType else untypedTreeType
        | _ -> untypedTreeType
    // We don't want to duplicate any global constraints, see #57
    | Type.WithGlobalConstraints _, Type.Var _ -> typedTreeType
    | _ -> untypedTreeType

let mkTypeForValNodeBasedOnTypedTree (typedTreeInfo : TypedTreeInfo) (parameters : Pattern list) : Type =
    // The `returnType` constructed from the typed tree cannot be trusted 100%.
    // We might receive `int -> int -> int` while the Oak only contained a single parameter.
    // This needs to be transformed to `int -> (int -> int)` to reflect that the return type actually is a function type.
    let returnTypeCorrectByActualParameters =
        let correctReturnTypeByActualParameters (returnType : Type) =
            match returnType with
            | Type.Funs funsNode ->
                if funsNode.Parameters.Length = parameters.Length then
                    returnType
                else
                    // We need to shift the extra parameters to the funsNode.ReturnType
                    let actualParameters, additionalTypes =
                        List.take parameters.Length funsNode.Parameters,
                        funsNode.Parameters |> List.skip parameters.Length

                    let actualReturnType =
                        TypeParenNode (
                            stn "(",
                            TypeFunsNode (additionalTypes, funsNode.ReturnType, zeroRange) |> Type.Funs,
                            stn ")",
                            zeroRange
                        )
                        |> Type.Paren

                    TypeFunsNode (actualParameters, actualReturnType, zeroRange) |> Type.Funs

            | _ -> returnType

        mapTypeWithGlobalConstraintsNode correctReturnTypeByActualParameters typedTreeInfo.ReturnType

    // Try and insert found information from the untyped tree.
    // The typed tree won't give us any parameter names for example.
    let returnTypeWithParameterNames =
        let rec updateParameter
            // Only top level tuples can have parameter names
            (isTopLevel : bool)
            (pattern : Pattern)
            (typeTreeType : Type)
            : Type
            =
            match pattern with
            | Pattern.Paren parenNode -> updateParameter isTopLevel parenNode.Pattern typeTreeType

            | Pattern.Named namedNode ->
                Type.SignatureParameter (
                    TypeSignatureParameterNode (None, Some namedNode.Name, typeTreeType, zeroRange)
                )

            | Pattern.OptionalVal optionalValNode ->
                let t =
                    // Remove the `option` if the type is `int option`.
                    // Because the parameter name will already start with an "?" like `?x`.
                    match typeTreeType with
                    | Type.AppPostfix appPostFix ->
                        match appPostFix.Last with
                        | Type.LongIdent optionType ->
                            match optionType.Content with
                            | [ IdentifierOrDot.Ident optionIdent ] when optionIdent.Text = "option" -> appPostFix.First
                            | _ -> typeTreeType
                        | _ -> typeTreeType
                    | _ -> typeTreeType

                Type.SignatureParameter (TypeSignatureParameterNode (None, Some optionalValNode, t, zeroRange))

            | Pattern.Parameter parameter ->
                let parameterType =
                    // Sometimes the type in the untyped tree is more accurate than what the typed tree returned.
                    // For example `System.Text.RegularExpressions.Regex` by untyped tree versus `Regex` by typed.
                    match parameter.Type with
                    | None -> typeTreeType
                    | Some untypedTreeType -> updateTypeBasedOnUnTyped typeTreeType untypedTreeType

                match parameter.Pattern with
                | Pattern.Named namedNode ->
                    Type.SignatureParameter (
                        TypeSignatureParameterNode (parameter.Attributes, Some namedNode.Name, parameterType, zeroRange)
                    )
                | Pattern.OptionalVal optValNode ->
                    Type.SignatureParameter (
                        TypeSignatureParameterNode (parameter.Attributes, Some optValNode, parameterType, zeroRange)
                    )
                | _ -> typeTreeType
            | Pattern.Tuple patTupleNode ->
                let tuplePatterns =
                    patTupleNode.Items
                    |> List.choose (
                        function
                        | Choice1Of2 p -> Some p
                        | Choice2Of2 _ -> None
                    )

                match typeTreeType with
                | Type.Tuple typeTupleNode when (isTopLevel && tuplePatterns.Length = typeTupleNode.Types.Length) ->
                    (tuplePatterns, typeTupleNode.Types)
                    ||> List.zip
                    |> List.map (fun (pat, t) -> updateParameter false pat t)
                    |> fun ts ->
                        let mutable collector = ListCollector<Choice<_, _>> ()

                        let rec visit ts =
                            match ts with
                            | [] -> ()
                            | [ last ] -> collector.Add (Choice1Of2 last)
                            | head :: rest ->
                                collector.Add (Choice1Of2 head)
                                collector.Add (Choice2Of2 (stn "*"))
                                visit rest

                        visit ts
                        TypeTupleNode (collector.Close (), zeroRange) |> Type.Tuple

                | _ -> typeTreeType
            | _ -> typeTreeType

        let updateParameters (t : Type) =
            match t with
            | Type.Funs funsNode ->
                let parameters =
                    funsNode.Parameters
                    |> List.mapi (fun idx (typeTreeType, arrow) ->
                        if idx > parameters.Length - 1 then
                            typeTreeType, arrow
                        else
                            // We might be able to replace the parameter name with the name we found in the Oak.
                            (updateParameter true parameters.[idx] typeTreeType), arrow
                    )

                TypeFunsNode (parameters, funsNode.ReturnType, zeroRange) |> Type.Funs
            | _ -> t

        mapTypeWithGlobalConstraintsNode updateParameters returnTypeCorrectByActualParameters

    returnTypeWithParameterNames

let stripParens (t : Type) =
    let strip t =
        match t with
        | Type.Paren parenNode -> parenNode.Type
        | _ -> t

    mapTypeWithGlobalConstraintsNode strip t

let mkTypeForValAux (bindingInfo : BindingInfo) (parameters : Pattern list) : Result<Type, string> =
    mkTypeFromString bindingInfo.ReturnTypeString
    |> Result.map (fun typeFromString ->
        let t =
            typeFromString
            // Type may have unwanted parentheses.
            |> stripParens

        let typedTreeInfo =
            {
                ReturnType = t
                BindingGenericParameters = bindingInfo.BindingGenericParameters
                TypeGenericParameters = bindingInfo.TypeGenericParameters
            }

        let returnType = mkTypeForValNodeBasedOnTypedTree typedTreeInfo parameters

        let returnType =
            // If the return parameter of a function type is a function type, we need to wrap it in parenthesis.
            // See test ``function return type``
            match returnType with
            | Type.Funs funsNode ->
                match funsNode.ReturnType with
                | Type.Funs _ ->
                    let parenNode = TypeParenNode (stn "(", funsNode.ReturnType, stn ")", zeroRange)
                    TypeFunsNode (funsNode.Parameters, Type.Paren parenNode, zeroRange) |> Type.Funs
                | _ -> returnType
            | _ -> returnType

        returnType
    )

let mkTypeForValNode
    (resolver : TypedTreeInfoResolver)
    (nameRange : range)
    (parameters : Pattern list)
    : Result<Type, string>
    =
    resolver.GetFullForBinding nameRange.FCSRange
    |> Result.bind (fun bindingInfo -> mkTypeForValAux bindingInfo parameters)

let mkTypeForGetSetMemberValNode
    (resolver : TypedTreeInfoResolver)
    (name : string)
    (nameRange : range)
    (parameters : Pattern list)
    : Result<Type, string>
    =
    resolver.GetPropertyWithIndex name nameRange.FCSRange
    |> Result.bind (fun bindingInfo -> mkTypeForValAux bindingInfo parameters)