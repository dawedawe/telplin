﻿module rec Telplin.Core.UntypedTree.Writer

open Fantomas.FCS.Text
open Fantomas.Core
open Fantomas.Core.SyntaxOak
open Microsoft.FSharp.Core
open Telplin.Core
open Telplin.Core.TypedTree.Resolver
open Telplin.Core.UntypedTree.ASTCreation
open Telplin.Core.UntypedTree.SourceParser

let processResults<'T, 'TResult>
    (foldFn : 'T -> 'TResult list -> TelplinError list -> 'TResult list * TelplinError list)
    (items : 'T list)
    : 'TResult list * TelplinError list
    =
    (items, (List.empty<'TResult>, List.empty<TelplinError>))
    ||> List.foldBack (fun item (results : 'TResult list, errors : TelplinError list) -> foldFn item results errors)

let mkLeadingKeywordForProperty (propertyNode : MemberDefnPropertyGetSetNode) =
    let hasDefault =
        propertyNode.LeadingKeyword.Content
        |> List.exists (fun stn -> stn.Text = "default")

    if hasDefault then
        mtn "override"
    else
        propertyNode.LeadingKeyword

let getLastIdentFromList (identList : IdentListNode) =
    match identList.Content with
    | [ IdentifierOrDot.Ident name ] -> name
    | _ -> failwith "todo, 38A9012C-2C4D-4387-9558-F75F6578402A"

/// Strip any generated argument names
let rec sanitizeReturnType (untypedParameters : Pattern list) (t : Type) : Type =
    let updateParameter (up : Pattern) (tp : Type) =
        match up, tp with
        | PatParen (Pattern.Parameter up), Type.SignatureParameter tp ->
            TypeSignatureParameterNode (up.Attributes, tp.Identifier, tp.Type, tp.Range)
            |> Type.SignatureParameter
        | _, Type.SignatureParameter tp ->
            // Remove the identifier if it starts with an _arg, this means it was generated by the compiler
            let identifier =
                match tp.Identifier with
                | Some name when name.Text.StartsWith "_arg" -> None
                | _ -> tp.Identifier

            TypeSignatureParameterNode (tp.Attributes, identifier, tp.Type, tp.Range)
            |> Type.SignatureParameter

        // use the name of the untyped parameter if the typed parameter is not a parameter type.
        | NameOfPat name, tp ->
            TypeSignatureParameterNode (None, Some name, tp, zeroRange)
            |> Type.SignatureParameter
        | _ -> tp

    match t with
    | Type.Funs funsNode ->
        let untypedParameters =
            if (untypedParameters.Length = funsNode.Parameters.Length) then
                untypedParameters
            else
                // Add wildcard to even things out
                let remainingParameters =
                    List.init (funsNode.Parameters.Length - untypedParameters.Length) (fun _ -> Pattern.Wild (stn "-"))

                [ yield! untypedParameters ; yield! remainingParameters ]

        assert (untypedParameters.Length = funsNode.Parameters.Length)

        let parameters =
            (untypedParameters, funsNode.Parameters)
            ||> List.zip
            |> List.map (fun (ut, (tt, arrow)) -> updateParameter ut tt, arrow)

        TypeFunsNode (parameters, funsNode.ReturnType, funsNode.Range) |> Type.Funs

    | Type.WithGlobalConstraints globalConstraintsNode ->
        TypeWithGlobalConstraintsNode (
            sanitizeReturnType untypedParameters globalConstraintsNode.Type,
            globalConstraintsNode.TypeConstraints,
            globalConstraintsNode.Range
        )
        |> Type.WithGlobalConstraints

    | _ -> t

[<RequireQualifiedAccess>]
type MemberDefnResult =
    | None
    | SingleMember of MemberDefn
    | GetAndSetMember of MemberDefn * MemberDefn
    | Error of TelplinError

let mkMember (resolver : TypedTreeInfoResolver) (md : MemberDefn) : MemberDefnResult =
    let mdRange = (MemberDefn.Node md).Range

    match md with
    | MemberDefn.ValField _
    | MemberDefn.AbstractSlot _
    | MemberDefn.Inherit _ -> MemberDefnResult.SingleMember md
    | MemberDefn.LetBinding _
    | MemberDefn.DoExpr _ -> MemberDefnResult.None

    | MemberDefn.ImplicitInherit implicitInherit ->
        let t =
            match implicitInherit with
            | InheritConstructor.Unit inheritCtor -> inheritCtor.Type
            | InheritConstructor.Paren inheritCtor -> inheritCtor.Type
            | InheritConstructor.Other inheritCtor -> inheritCtor.Type
            | InheritConstructor.TypeOnly inheritCtor -> inheritCtor.Type

        MemberDefnInheritNode (implicitInherit.InheritKeyword, t, zeroRange)
        |> MemberDefn.Inherit
        |> MemberDefnResult.SingleMember

    | MemberDefn.Member bindingNode ->
        match bindingNode.FunctionName with
        | Choice2Of2 _ -> MemberDefnResult.None
        | Choice1Of2 name ->
            let name =
                match name.Content with
                // member this.Foo
                | [ IdentifierOrDot.Ident _
                    (IdentifierOrDot.KnownDot _ | IdentifierOrDot.UnknownDot)
                    IdentifierOrDot.Ident name ]
                // static member Foo
                | [ IdentifierOrDot.Ident name ] -> name
                | _ -> failwith "todo, 38A9012C-2C4D-4387-9558-F75F6578402A"

            let sigMemberResult =
                resolver.GetValText (name.Text, name.Range.FCSRange)
                |> Result.bind mkMemberSigFromString

            match sigMemberResult with
            | Error error -> MemberDefnResult.Error (TelplinError (mdRange, error))
            | Ok sigMember ->

            let valNode = sigMember.Val

            MemberDefnSigMemberNode (
                ValNode (
                    bindingNode.XmlDoc,
                    bindingNode.Attributes,
                    Some bindingNode.LeadingKeyword,
                    bindingNode.Inline,
                    bindingNode.IsMutable,
                    bindingNode.Accessibility,
                    valNode.Identifier,
                    valNode.TypeParams,
                    sanitizeReturnType bindingNode.Parameters valNode.Type,
                    valNode.Equals,
                    valNode.Expr,
                    valNode.Range
                ),
                sigMember.WithGetSet,
                sigMember.Range
            )
            |> MemberDefn.SigMember
            |> MemberDefnResult.SingleMember

    | MemberDefn.AutoProperty autoProperty ->
        let sigMemberResult =
            resolver.GetValText (autoProperty.Identifier.Text, autoProperty.Identifier.Range.FCSRange)
            |> Result.bind mkMemberSigFromString

        match sigMemberResult with
        | Error error -> MemberDefnResult.Error (TelplinError (mdRange, error))
        | Ok sigMember ->

        let valNode = sigMember.Val

        let valKw =
            autoProperty.LeadingKeyword.Content
            |> List.filter (fun stn -> stn.Text <> "val")
            |> fun keywords -> MultipleTextsNode (keywords, autoProperty.LeadingKeyword.Range)

        MemberDefnSigMemberNode (
            ValNode (
                autoProperty.XmlDoc,
                autoProperty.Attributes,
                Some valKw,
                valNode.Inline,
                valNode.IsMutable,
                autoProperty.Accessibility,
                autoProperty.Identifier,
                valNode.TypeParams,
                valNode.Type,
                valNode.Equals,
                valNode.Expr,
                valNode.Range
            ),
            autoProperty.WithGetSet,
            sigMember.Range
        )
        |> MemberDefn.SigMember
        |> MemberDefnResult.SingleMember

    | PrivateMemberDefnExplicitCtor when not resolver.IncludePrivateBindings -> MemberDefnResult.None

    | MemberDefn.ExplicitCtor explicitNode ->
        let sigMemberResult =
            resolver.GetValText (explicitNode.New.Text, explicitNode.New.Range.FCSRange)
            |> Result.bind mkMemberSigFromString

        match sigMemberResult with
        | Error error -> MemberDefnResult.Error (TelplinError (mdRange, error))
        | Ok sigMember ->

        let valNode = sigMember.Val
        let name = explicitNode.New

        MemberDefnSigMemberNode (
            ValNode (
                explicitNode.XmlDoc,
                explicitNode.Attributes,
                None,
                None,
                false,
                explicitNode.Accessibility,
                name,
                None,
                valNode.Type,
                Some (stn "="),
                None,
                zeroRange
            ),
            None,
            zeroRange
        )
        |> MemberDefn.SigMember
        |> MemberDefnResult.SingleMember

    | MemberDefn.Interface interfaceNode ->
        MemberDefnInterfaceNode (interfaceNode.Interface, interfaceNode.Type, None, [], zeroRange)
        |> MemberDefn.Interface
        |> MemberDefnResult.SingleMember

    // We need to create two val in this case, see #52
    | PropertyGetSetWithExtraParameter (propertyNode, getBinding, setBinding) ->
        let name =
            match List.tryLast propertyNode.MemberName.Content with
            | Some (IdentifierOrDot.Ident name) -> name
            | _ -> failwith "Property does not have a name?"

        let leadingKeyword = mkLeadingKeywordForProperty propertyNode

        let mkSigMember getOrSet =
            resolver.GetValText ($"%s{getOrSet}_{name.Text}", name.Range.FCSRange)
            |> Result.bind mkMemberSigFromString
            |> Result.map (fun sigMember ->
                let valNode = sigMember.Val

                MemberDefnSigMemberNode (
                    ValNode (
                        propertyNode.XmlDoc,
                        propertyNode.Attributes,
                        Some leadingKeyword,
                        None,
                        false,
                        None,
                        name,
                        None,
                        valNode.Type,
                        Some (stn "="),
                        None,
                        zeroRange
                    ),
                    Some (MultipleTextsNode ([ stn "with" ; stn getOrSet ], zeroRange)),
                    zeroRange
                )
                |> MemberDefn.SigMember
            )

        let getSigMemberResult = mkSigMember "get"
        let setSigMemberResult = mkSigMember "set"

        match getSigMemberResult, setSigMemberResult with
        | Error error, Ok _
        | Ok _, Error error
        | Error error, Error _ -> MemberDefnResult.Error (TelplinError (mdRange, error))
        | Ok getSigMember, Ok setSigMember ->

        let sigs =
            if Position.posGt getBinding.Range.Start setBinding.Range.Start then
                getSigMember, setSigMember
            else
                setSigMember, getSigMember

        MemberDefnResult.GetAndSetMember sigs

    | MemberDefn.PropertyGetSet propertyNode ->
        let name =
            match List.tryLast propertyNode.MemberName.Content with
            | Some (IdentifierOrDot.Ident name) -> name
            | _ -> failwith "Property does not have a name?"

        let leadingKeyword = mkLeadingKeywordForProperty propertyNode

        let sigMemberResult =
            resolver.GetValText (name.Text, name.Range.FCSRange)
            |> Result.bind mkMemberSigFromString

        match sigMemberResult with
        | Error error -> MemberDefnResult.Error (TelplinError (mdRange, error))
        | Ok sigMember ->

        let valNode = sigMember.Val

        let withGetSet =
            match propertyNode.LastBinding with
            | None -> [ propertyNode.FirstBinding.LeadingKeyword ]
            | Some lastBinding ->
                [
                    stn $"%s{propertyNode.FirstBinding.LeadingKeyword.Text},"
                    lastBinding.LeadingKeyword
                ]

        MemberDefnSigMemberNode (
            ValNode (
                propertyNode.XmlDoc,
                propertyNode.Attributes,
                Some leadingKeyword,
                None,
                false,
                None,
                name,
                None,
                valNode.Type,
                Some (stn "="),
                None,
                zeroRange
            ),
            Some (MultipleTextsNode ([ stn "with" ; yield! withGetSet ], zeroRange)),
            zeroRange
        )
        |> MemberDefn.SigMember
        |> MemberDefnResult.SingleMember

    | md -> MemberDefnResult.Error (TelplinError (mdRange, $"Not implemented MemberDefn: %A{md}"))

let mkMembers (resolver : TypedTreeInfoResolver) (ms : MemberDefn list) : MemberDefn list * TelplinError list =
    processResults
        (fun md (sigMembers : MemberDefn list) (errors : TelplinError list) ->
            match mkMember resolver md with
            | MemberDefnResult.None -> sigMembers, errors
            | MemberDefnResult.Error error -> sigMembers, error :: errors
            | MemberDefnResult.SingleMember md -> md :: sigMembers, errors
            | MemberDefnResult.GetAndSetMember (g, s) -> s :: g :: sigMembers, errors
        )
        ms

/// <summary>
/// Map a TypeDefn to its signature counterpart.
/// A lot of information can typically be re-used when the same syntax applies.
/// The most important things that need mapping are the implicit constructor and the type members.
/// </summary>
/// <param name="resolver">Resolves information from the Typed tree.</param>
/// <param name="forceAndKeyword">In case of a recursive nested module, subsequent types need the `and` keyword.</param>>
/// <param name="typeDefn">Type definition DU case from the Untyped tree.</param>
let mkTypeDefn
    (resolver : TypedTreeInfoResolver)
    (forceAndKeyword : bool)
    (typeDefn : TypeDefn)
    : TypeDefn * TelplinError list
    =
    let tdn = TypeDefn.TypeDefnNode typeDefn

    let typeName =
        // To overcome
        // "typecheck error The representation of this type is hidden by the signature."
        // It must be given an attribute such as [<Sealed>], [<Class>] or [<Interface>] to indicate the characteristics of the type.
        // We insert an additional `[<Class>]` attribute when no constructor is present for a TypeDefn.Regular.
        let attributes =
            let hasExistingAttribute =
                hasAnyAttribute
                    (set [| "Class" ; "ClassAttribute" ; "Struct" ; "StructAttribute" |])
                    tdn.TypeName.Attributes

            let allMembersAreAbstractOrInherit =
                tdn.Members
                |> List.forall (
                    function
                    | MemberDefn.AbstractSlot _
                    | MemberDefn.Inherit _ -> true
                    | _ -> false
                )

            match typeDefn with
            | TypeDefn.Regular _ ->
                if
                    tdn.TypeName.ImplicitConstructor.IsSome
                    || hasExistingAttribute
                    || allMembersAreAbstractOrInherit
                then
                    tdn.TypeName.Attributes
                else
                    addAttribute "Class" tdn.TypeName.Attributes
            | _ -> tdn.TypeName.Attributes

        // To overcome: warning FS1178: The struct, record or union type is not structurally comparable because the type 'obj' does not satisfy the 'comparison' constraint.
        // Consider adding the 'NoComparison' attribute to the type to clarify that the type is not comparable
        let attributes =
            let isStructWithoutComparisonResult =
                resolver.IsStructWithoutComparison tdn.TypeName.Identifier.Range.FCSRange

            match isStructWithoutComparisonResult with
            | Error _ -> attributes
            | Ok false -> attributes
            | Ok true ->

            let hasExistingNoComparisonAttribute =
                hasAnyAttribute (set [| "NoComparison" ; "NoComparisonAttribute" |]) attributes

            if hasExistingNoComparisonAttribute then
                attributes
            else
                addAttribute "NoComparison" attributes

        let leadingKeyword =
            if forceAndKeyword then
                stn "and"
            else
                tdn.TypeName.LeadingKeyword

        TypeNameNode (
            tdn.TypeName.XmlDoc,
            attributes,
            leadingKeyword,
            tdn.TypeName.Accessibility,
            tdn.TypeName.Identifier,
            tdn.TypeName.TypeParameters,
            tdn.TypeName.Constraints,
            None,
            tdn.TypeName.EqualsToken,
            tdn.TypeName.WithKeyword,
            zeroRange
        )

    let mkImplicitCtor
        (resolver : TypedTreeInfoResolver)
        (identifier : IdentListNode)
        (implicitCtor : ImplicitConstructorNode)
        : Result<MemberDefn, TelplinError>
        =
        let sigMemberResult =
            resolver.GetValTextForConstructor identifier.Range.FCSRange
            |> Result.bind mkPrimaryConstructorFromString

        match sigMemberResult with
        | Error error -> TelplinError (implicitCtor.Range, error) |> Result.Error
        | Ok sigMember -> MemberDefn.SigMember sigMember |> Result.Ok

    match typeDefn with
    | TypeDefn.Record recordNode ->
        let members, memberErrors = mkMembers resolver tdn.Members

        let sigRecord =
            TypeDefnRecordNode (
                typeName,
                recordNode.Accessibility,
                recordNode.OpeningBrace,
                recordNode.Fields,
                recordNode.ClosingBrace,
                members,
                zeroRange
            )
            |> TypeDefn.Record

        sigRecord, memberErrors

    | TypeDefn.Explicit explicitNode ->

        let members, memberErrors =
            let members, memberErrors = mkMembers resolver explicitNode.Body.Members

            match tdn.TypeName.ImplicitConstructor with
            | Some PrivateConstructor when not resolver.IncludePrivateBindings -> members, memberErrors
            | None -> members, memberErrors
            | Some implicitCtor ->
                match mkImplicitCtor resolver tdn.TypeName.Identifier implicitCtor with
                | Error error -> members, error :: memberErrors
                | Ok sigCtor -> sigCtor :: members, memberErrors

        let body =
            TypeDefnExplicitBodyNode (explicitNode.Body.Kind, members, explicitNode.Body.End, zeroRange)

        let extraMembers, extraMemberErrors = mkMembers resolver tdn.Members

        let sigExplicit =
            TypeDefnExplicitNode (typeName, body, extraMembers, zeroRange)
            |> TypeDefn.Explicit

        sigExplicit, (memberErrors @ extraMemberErrors)

    | TypeDefn.Regular _ ->
        let members, memberErrors =
            let members, memberErrors = mkMembers resolver tdn.Members

            match tdn.TypeName.ImplicitConstructor with
            | None -> members, memberErrors
            | Some PrivateConstructor when not resolver.IncludePrivateBindings -> members, memberErrors
            | Some implicitCtor ->
                match mkImplicitCtor resolver tdn.TypeName.Identifier implicitCtor with
                | Error error -> members, error :: memberErrors
                | Ok sigCtor -> sigCtor :: members, memberErrors

        let sigRegular =
            TypeDefnRegularNode (typeName, members, zeroRange) |> TypeDefn.Regular

        sigRegular, memberErrors

    | TypeDefn.Union unionNode ->
        let members, memberErrors = mkMembers resolver tdn.Members

        let sigUnion =
            TypeDefnUnionNode (typeName, unionNode.Accessibility, unionNode.UnionCases, members, zeroRange)
            |> TypeDefn.Union

        sigUnion, memberErrors

    | TypeDefn.Abbrev abbrevNode ->
        let members, memberErrors = mkMembers resolver tdn.Members

        let sigAbbrev =
            TypeDefnAbbrevNode (typeName, abbrevNode.Type, members, zeroRange)
            |> TypeDefn.Abbrev

        sigAbbrev, memberErrors

    | TypeDefn.Augmentation _ ->
        let members, memberErrors = mkMembers resolver tdn.Members

        let sigAugmentation =
            TypeDefnAugmentationNode (typeName, members, zeroRange) |> TypeDefn.Augmentation

        sigAugmentation, memberErrors

    | TypeDefn.None _ -> (TypeDefn.None tdn.TypeName), []
    | TypeDefn.Enum _
    | TypeDefn.Delegate _ -> typeDefn, []

[<RequireQualifiedAccess>]
type ModuleDeclResult =
    | None
    | SingleModuleDecl of ModuleDecl
    | Error of TelplinError
    | Nested of parent : ModuleDecl * childErrors : TelplinError list

let mkModuleDecl (resolver : TypedTreeInfoResolver) (mdl : ModuleDecl) : ModuleDeclResult =
    let mdlRange = (ModuleDecl.Node mdl).Range

    match mdl with
    | ModuleDecl.DeclExpr _
    | ModuleDecl.Attributes _ -> ModuleDeclResult.None
    | ModuleDecl.OpenList _
    | ModuleDecl.Val _
    | ModuleDecl.HashDirectiveList _
    | ModuleDecl.ModuleAbbrev _ -> ModuleDeclResult.SingleModuleDecl mdl
    | PrivateTopLevelBinding when not resolver.IncludePrivateBindings -> ModuleDeclResult.None
    | ModuleDecl.TopLevelBinding bindingNode ->
        match bindingNode.FunctionName with
        | Choice1Of2 name ->
            let valKw = mtn "val"
            let name = getLastIdentFromList name

            let valNodeResult =
                resolver.GetValText (name.Text, name.Range.FCSRange)
                |> Result.bind mkValFromString

            match valNodeResult with
            | Error error -> ModuleDeclResult.Error (TelplinError (mdlRange, error))
            | Ok valNode ->

            let expr =
                if hasAnyAttribute (Set.singleton "Literal") bindingNode.Attributes then
                    Some bindingNode.Expr
                else
                    None

            ValNode (
                bindingNode.XmlDoc,
                bindingNode.Attributes,
                Some valKw,
                bindingNode.Inline,
                bindingNode.IsMutable,
                bindingNode.Accessibility,
                name,
                valNode.TypeParams,
                sanitizeReturnType bindingNode.Parameters valNode.Type,
                Some (stn "="),
                expr,
                zeroRange
            )
            |> ModuleDecl.Val
            |> ModuleDeclResult.SingleModuleDecl
        | Choice2Of2 _ -> ModuleDeclResult.Error (TelplinError (mdlRange, "Pattern identifiers are not supported"))

    | ModuleDecl.TypeDefn typeDefn ->
        let sigTypeDefn, memberErrors = mkTypeDefn resolver false typeDefn
        ModuleDeclResult.Nested (ModuleDecl.TypeDefn sigTypeDefn, memberErrors)

    | ModuleDecl.NestedModule nestedModule ->
        let sigs, errors =
            if not nestedModule.IsRecursive then
                processResults
                    (fun decl sigs errors ->
                        match mkModuleDecl resolver decl with
                        | ModuleDeclResult.None -> sigs, errors
                        | ModuleDeclResult.SingleModuleDecl sigDecl -> sigDecl :: sigs, errors
                        | ModuleDeclResult.Error error -> sigs, error :: errors
                        | ModuleDeclResult.Nested (sigDecl, nestedErrors) -> sigDecl :: sigs, nestedErrors @ errors
                    )
                    nestedModule.Declarations
            else
                // A nested module cannot be recursive in a signature file.
                // Any subsequent types (SynModuleDecl.Types) should be transformed to use the `and` keyword.
                let rec visit
                    (lastItemIsType : bool)
                    (decls : ModuleDecl list)
                    (continuation : ModuleDecl list * TelplinError list -> ModuleDecl list * TelplinError list)
                    : ModuleDecl list * TelplinError list
                    =
                    match decls with
                    | [] -> continuation ([], [])
                    | currentDecl :: nextDecls ->

                    let isType, declResult =
                        match currentDecl with
                        | ModuleDecl.TypeDefn typeDefnNode ->
                            let sigTypeDefn, errors = mkTypeDefn resolver lastItemIsType typeDefnNode

                            true, ModuleDeclResult.Nested (ModuleDecl.TypeDefn sigTypeDefn, errors)
                        | decl -> false, mkModuleDecl resolver decl

                    visit
                        isType
                        nextDecls
                        (fun (sigs, errors) ->
                            match declResult with
                            | ModuleDeclResult.None -> sigs, errors
                            | ModuleDeclResult.SingleModuleDecl sigDecl -> sigDecl :: sigs, errors
                            | ModuleDeclResult.Error error -> sigs, error :: errors
                            | ModuleDeclResult.Nested (sigDecl, nestedErrors) -> sigDecl :: sigs, nestedErrors @ errors
                            |> continuation
                        )

                visit false nestedModule.Declarations id

        let sigNestedModule =
            NestedModuleNode (
                nestedModule.XmlDoc,
                nestedModule.Attributes,
                nestedModule.Module,
                nestedModule.Accessibility,
                false,
                nestedModule.Identifier,
                nestedModule.Equals,
                sigs,
                zeroRange
            )
            |> ModuleDecl.NestedModule

        ModuleDeclResult.Nested (sigNestedModule, errors)
    | ModuleDecl.Exception exceptionNode ->
        let sigMembers, errors = mkMembers resolver exceptionNode.Members

        let sigException =
            ExceptionDefnNode (
                exceptionNode.XmlDoc,
                exceptionNode.Attributes,
                exceptionNode.Accessibility,
                exceptionNode.UnionCase,
                exceptionNode.WithKeyword,
                sigMembers,
                zeroRange
            )
            |> ModuleDecl.Exception

        ModuleDeclResult.Nested (sigException, errors)
    | ModuleDecl.ExternBinding externBindingNode ->
        let nameRange = externBindingNode.Identifier.Range
        let name = getLastIdentFromList externBindingNode.Identifier

        let valNodeResult =
            resolver.GetValText (name.Text, nameRange.FCSRange)
            |> Result.bind mkValFromString

        match valNodeResult with
        | Error error -> ModuleDeclResult.Error (TelplinError (mdlRange, error))
        | Ok valNode ->

        ValNode (
            externBindingNode.XmlDoc,
            externBindingNode.Attributes,
            Some (mtn "val"),
            None,
            false,
            externBindingNode.Accessibility,
            name,
            None,
            valNode.Type,
            Some (stn "="),
            None,
            zeroRange
        )
        |> ModuleDecl.Val
        |> ModuleDeclResult.SingleModuleDecl

let mkModuleOrNamespace
    (resolver : TypedTreeInfoResolver)
    (moduleNode : ModuleOrNamespaceNode)
    : ModuleOrNamespaceNode * TelplinError list
    =
    let decls, errors =
        processResults
            (fun mdl sigs errors ->
                match mkModuleDecl resolver mdl with
                | ModuleDeclResult.None -> sigs, errors
                | ModuleDeclResult.SingleModuleDecl sigDecl -> sigDecl :: sigs, errors
                | ModuleDeclResult.Error error -> sigs, error :: errors
                | ModuleDeclResult.Nested (sigDecl, childErrors) -> sigDecl :: sigs, childErrors @ errors
            )
            moduleNode.Declarations

    ModuleOrNamespaceNode (moduleNode.Header, decls, zeroRange), errors

let mkSignatureFile (resolver : TypedTreeInfoResolver) (code : string) : string * TelplinError list =
    let ast, _diagnostics =
        Fantomas.FCS.Parse.parseFile false (SourceText.ofString code) resolver.Defines

    let implementationOak = CodeFormatter.TransformAST (ast, code)

    let signatureOak, (errors : TelplinError list) =
        let mdns, errors =
            List.map (mkModuleOrNamespace resolver) implementationOak.ModulesOrNamespaces
            |> List.unzip

        Oak (implementationOak.ParsedHashDirectives, mdns, zeroRange), List.concat errors

    let code = CodeFormatter.FormatOakAsync signatureOak |> Async.RunSynchronously
    code, errors
