namespace Foq

open System
open System.Reflection
open Microsoft.FSharp.Reflection

/// Mock object interface for verification
type IMockObject =
    abstract Invocations : Invocations
    [<CLIEvent>]
    abstract Invoked : IEvent<EventHandler,EventArgs>
    abstract Verifiers : Verifiers
/// Member invocation record
and Invocation = { Method : MethodBase; Args : obj[] }
/// List of invocations
and Invocations = System.Collections.Generic.List<Invocation>
/// List of verifiers
and Verifiers = System.Collections.Generic.List<Action>

module internal Emit =
    open System.Reflection.Emit
    /// Boxed value
    type Value = obj
    /// Boxed function
    type Func = obj
    /// Boxed event
    type PublishedEvent = obj
    /// Method argument type
    type Arg = Any | Arg of Value | OutArg of Value | Pred of Func | PredUntyped of Func
    /// Method result type
    type Result = 
        | Unit
        | ReturnValue of Value * Type
        | ReturnFunc of Func
        | Handler of string * PublishedEvent
        | Call of Func
        | Raise of Type
        | RaiseValue of exn

    /// Generates constructor
    let generateConstructor (typeBuilder:TypeBuilder) ps (genBody:ILGenerator -> unit) =
        let cons = typeBuilder.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,ps)
        let il = cons.GetILGenerator()
        // Generate body
        genBody il
        il.Emit(OpCodes.Ret)

    /// Defines method
    let defineMethod (typeBuilder:TypeBuilder) (abstractMethod:MethodInfo) =
        let attr =
            MethodAttributes.Public ||| MethodAttributes.HideBySig ||| MethodAttributes.Virtual
        let args = abstractMethod.GetParameters() |> Array.map (fun arg -> arg.ParameterType)
        let m = typeBuilder.DefineMethod(abstractMethod.Name, attr, abstractMethod.ReturnType, args)
        if abstractMethod.IsGenericMethod then
            let names = abstractMethod.GetGenericArguments() |> Array.map (fun x -> x.Name)
            m.DefineGenericParameters(names) |> ignore
        m

    /// Generates method overload args match
    let generateArgs 
        (il:ILGenerator) (argsLookup:ResizeArray<Value[]>,argsField:FieldBuilder) 
        (mi:MethodInfo,args) (unmatched:Label) =
        /// Index of argument values for current method overload
        let argsLookupIndex = argsLookup.Count
        // Add arguments to lookup
        args 
        |> Array.map (function Any -> null | Arg(value) -> value | OutArg(value) -> value | Pred(f) -> f | PredUntyped(f) -> f) 
        |> argsLookup.Add
        // Emit argument matching
        args |> Seq.iteri (fun argIndex arg ->
            let emitArgBox () =
                il.Emit(OpCodes.Ldarg, argIndex+1)
                let pi = mi.GetParameters().[argIndex]
                il.Emit(OpCodes.Box, pi.ParameterType)
            let emitArgLookup () =
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Ldfld, argsField)
                il.Emit(OpCodes.Ldc_I4, argsLookupIndex)
                il.Emit(OpCodes.Ldelem_Ref)
                il.Emit(OpCodes.Ldc_I4, argIndex)
                il.Emit(OpCodes.Ldelem_Ref)
            match arg with
            | Any -> ()
            | Arg(value) ->
                emitArgBox ()
                emitArgLookup ()
                // Emit Object.Equals(box args.[argIndex+1], _args.[argsLookupIndex].[argIndex])
                il.EmitCall(OpCodes.Call, typeof<obj>.GetMethod("Equals",[|typeof<obj>;typeof<obj>|]), null) 
                il.Emit(OpCodes.Brfalse_S, unmatched)
            | OutArg(value) ->               
                il.Emit(OpCodes.Ldarg, argIndex+1)
                emitArgLookup ()             
                let pi = mi.GetParameters().[argIndex]
                let t = pi.ParameterType.GetElementType()
                il.Emit(OpCodes.Unbox_Any, t)
                il.Emit(OpCodes.Stobj, t)
            | Pred(f) ->
                emitArgLookup ()
                il.Emit(OpCodes.Ldarg, argIndex+1)
                let argType = mi.GetParameters().[argIndex].ParameterType
                let invoke = FSharpType.MakeFunctionType(argType,typeof<bool>).GetMethod("Invoke")
                il.Emit(OpCodes.Callvirt, invoke)
                il.Emit(OpCodes.Brfalse_S, unmatched)
            | PredUntyped(f) ->
                emitArgLookup ()
                il.Emit(OpCodes.Ldarg, argIndex+1)
                let argType = mi.GetParameters().[argIndex].ParameterType
                il.Emit(OpCodes.Box, argType)
                let invoke = FSharpType.MakeFunctionType(typeof<obj>,typeof<bool>).GetMethod("Invoke")
                il.Emit(OpCodes.Callvirt, invoke)
                il.Emit(OpCodes.Brfalse_S, unmatched)
        )

    /// Generates method return
    let generateReturn
        (il:ILGenerator) (returnValues:ResizeArray<Value>,returnValuesField:FieldBuilder) (mi:MethodInfo,result) =
        // Emits _returnValues.[returnValuesIndex]
        let emitReturnValueLookup value =
            let returnValuesIndex = returnValues.Count
            returnValues.Add(value)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldfld, returnValuesField)
            il.Emit(OpCodes.Ldc_I4, returnValuesIndex)
            il.Emit(OpCodes.Ldelem_Ref)
        /// Emits AddHandler/RemoveHandler
        let emitEventHandler handlerName e =
            emitReturnValueLookup e
            let handlerType = e.GetType().GetGenericArguments().[0]
            il.Emit(OpCodes.Ldarg_1)
            let t = typedefof<IDelegateEvent<_>>.MakeGenericType(handlerType)
            let invoke = t.GetMethod(handlerName)
            il.Emit(OpCodes.Callvirt, invoke)
            il.Emit(OpCodes.Ret)
        // Emit result
        match result with
        | Unit -> il.Emit(OpCodes.Ret)
        | ReturnValue(value, returnType) ->
            emitReturnValueLookup value
            il.Emit(OpCodes.Unbox_Any, returnType)
            il.Emit(OpCodes.Ret)
        | ReturnFunc(f) ->
            emitReturnValueLookup f
            // Emit Invoke
            il.Emit(OpCodes.Ldnull)
            let invoke = typeof<FSharpFunc<unit,obj>>.GetMethod("Invoke")
            il.Emit(OpCodes.Callvirt, invoke)
            if mi.ReturnType = typeof<unit> || mi.ReturnType = typeof<Void> then 
                il.Emit(OpCodes.Pop)
            il.Emit(OpCodes.Ret)
        | Handler(handlerName, e) -> emitEventHandler handlerName e
        | Call(f) ->
            emitReturnValueLookup f
            // Emit Invoke
            let args = mi.GetParameters() |> Array.map (fun arg -> arg.ParameterType)
            let returnType = if mi.ReturnType = typeof<Void> then typeof<unit> else mi.ReturnType
            let argsType =
                if args.Length = 1 then 
                    il.Emit(OpCodes.Ldarg_1)
                    args.[0]
                else
                    for i = 1 to args.Length do il.Emit(OpCodes.Ldarg, i)
                    il.Emit(OpCodes.Newobj, FSharpType.MakeTupleType(args).GetConstructor(args))
                    typeof<obj>
            let invoke = FSharpType.MakeFunctionType(argsType, returnType).GetMethod("Invoke")
            il.Emit(OpCodes.Callvirt, invoke)
            if mi.ReturnType = typeof<unit> || mi.ReturnType = typeof<Void> then il.Emit(OpCodes.Pop)
            il.Emit(OpCodes.Ret)
        | Raise(exnType) -> il.ThrowException(exnType)
        | RaiseValue(exnValue) ->
            emitReturnValueLookup exnValue
            il.Emit(OpCodes.Throw)

    /// Generates invocation add
    let generateAddInvocation
        (il:ILGenerator) (invocationsField:FieldBuilder) (abstractMethod:MethodInfo) =
        let ps = abstractMethod.GetParameters()
        // Create local array to store arguments
        let local0 = il.DeclareLocal(typeof<obj[]>).LocalIndex
        il.Emit(OpCodes.Ldc_I4, ps.Length)
        il.Emit(OpCodes.Newarr, typeof<obj>)
        il.Emit(OpCodes.Stloc,local0)
        // Store arguments
        for argIndex = 0 to ps.Length - 1 do
            il.Emit(OpCodes.Ldloc, local0)
            il.Emit(OpCodes.Ldc_I4, argIndex)
            il.Emit(OpCodes.Ldarg, argIndex + 1)
            let t = ps.[argIndex].ParameterType            
            if not t.IsByRef then il.Emit(OpCodes.Box, t)
            il.Emit(OpCodes.Stelem_Ref)
        // Add invocation to invocations list
        il.Emit(OpCodes.Ldarg_0)
        il.Emit(OpCodes.Ldfld, invocationsField)
        il.Emit(OpCodes.Call, typeof<MethodBase>.GetMethod("GetCurrentMethod"))
        il.Emit(OpCodes.Ldloc, local0)
        il.Emit(OpCodes.Newobj, typeof<Invocation>.GetConstructor([|typeof<MethodBase>;typeof<obj[]>|]))
        let invoke = typeof<System.Collections.Generic.List<obj[]>>.GetMethod("Add")
        il.Emit(OpCodes.Callvirt, invoke)

    /// Generates trigger
    let generateTrigger (il:ILGenerator) (invokedField:FieldBuilder) =
        il.Emit(OpCodes.Ldarg_0)
        il.Emit(OpCodes.Ldfld, invokedField)
        il.Emit(OpCodes.Ldarg_0)
        il.Emit(OpCodes.Newobj, typeof<EventArgs>.GetConstructor([||]))
        let trigger = typeof<Event<EventHandler,EventArgs>>.GetMethod("Trigger")
        il.Emit(OpCodes.Callvirt, trigger)

    /// Generates method overload
    let generateOverload 
        (il:ILGenerator)
        (argsLookup:ResizeArray<Value[]>,argsField:FieldBuilder)
        (returnValues:ResizeArray<Value>,returnValuesField:FieldBuilder) 
        (mi:MethodInfo,(args, result)) =
        /// Label to goto if argument fails
        let unmatched = il.DefineLabel()
        generateArgs il (argsLookup,argsField) (mi,args) unmatched
        generateReturn il (returnValues,returnValuesField) (mi,result)
        il.MarkLabel(unmatched)

    /// Defines a type builder for the specified abstract type
    let defineType (abstractType:Type) =
        /// Stub name for abstract type
        let mockName = "Mock." + abstractType.Name.Replace("'", "!")
        /// Builder for assembly
        let assemblyBuilder =
            AppDomain.CurrentDomain.DefineDynamicAssembly(AssemblyName(mockName),AssemblyBuilderAccess.Run)
        /// Builder for module
        let moduleBuilder = assemblyBuilder.DefineDynamicModule(mockName+".dll")
        let parent, interfaces = 
            if abstractType.IsInterface
            then typeof<obj>, [|abstractType|]
            else abstractType, [||]
        let attributes = TypeAttributes.Public ||| TypeAttributes.Class
        let interfaces = [|yield typeof<IMockObject>; yield! interfaces|]
        moduleBuilder.DefineType(mockName, attributes, parent, interfaces)

    /// Builds a mock from the specified calls
    let mock (isStrict, abstractType:Type, calls:(MethodInfo * (Arg[] * Result)) list, args:obj[]) =
        /// Builder for abstract type
        let typeBuilder = defineType abstractType
        /// Field settings
        let fields = FieldAttributes.Private ||| FieldAttributes.InitOnly 
        /// Field for method return values
        let returnValuesField = typeBuilder.DefineField("_returnValues", typeof<obj[]>, fields)
        /// Field for method arguments 
        let argsField = typeBuilder.DefineField("_args", typeof<obj[][]>, fields)
        /// Field for method invocations
        let invocationsField = typeBuilder.DefineField("_invocations", typeof<Invocations>, fields)
        /// Field for invoked event
        let invokedField = typeBuilder.DefineField("_invoked", typeof<Event<EventHandler,EventArgs>>, fields)
        /// Field for verifiers
        let verifiersField = typeBuilder.DefineField("_verifiers", typeof<Verifiers>, fields)
        // Generate default constructor
        generateConstructor typeBuilder [||] (fun il -> ())
        // Generates constructor body
        let generateConstructorBody (il:ILGenerator) =
            /// Constructor argument types
            let argTypes = [|for arg in args -> arg.GetType()|]
            // Call base constructor
            if args.Length = 0 then
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Call, typeof<obj>.GetConstructor(Type.EmptyTypes))
            else
                il.Emit(OpCodes.Ldarg_0)
                let bindings = 
                    BindingFlags.FlattenHierarchy ||| BindingFlags.Instance ||| 
                    BindingFlags.Public ||| BindingFlags.NonPublic 
                let ci = abstractType.GetConstructor(bindings, Type.DefaultBinder, argTypes, [||])
                argTypes |> Array.iteri (fun i arg ->
                    il.Emit(OpCodes.Ldarg_3) 
                    il.Emit(OpCodes.Ldc_I4, i) 
                    il.Emit(OpCodes.Ldelem_Ref)
                    il.Emit(OpCodes.Unbox_Any, arg)
                )
                il.Emit(OpCodes.Call, ci)
            // Set fields
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldarg_1)
            il.Emit(OpCodes.Stfld, returnValuesField)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldarg_2)
            il.Emit(OpCodes.Stfld, argsField)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Newobj, typeof<Invocations>.GetConstructor([||]))
            il.Emit(OpCodes.Stfld, invocationsField)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Newobj, typeof<Event<EventHandler,EventArgs>>.GetConstructor([||]))
            il.Emit(OpCodes.Stfld, invokedField)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Newobj, typeof<Verifiers>.GetConstructor([||]))
            il.Emit(OpCodes.Stfld, verifiersField)
        // Generate constructor overload
        let constructorArgs = [|typeof<obj[]>;typeof<obj[][]>;typeof<obj[]>|]
        generateConstructor typeBuilder constructorArgs generateConstructorBody
        /// Generates a property getter
        let generatePropertyGetter name (field:FieldBuilder) =
            let mi = (typeof<IMockObject>.GetProperty(name).GetGetMethod())
            let getter = defineMethod typeBuilder mi
            let il = getter.GetILGenerator()
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldfld, field)
            il.Emit(OpCodes.Ret)
        // Generate IMockObject.Invocations property getter
        generatePropertyGetter "Invocations" invocationsField
        // Generate IMockObject.Verifiers property getter
        generatePropertyGetter "Verifiers" verifiersField
        /// Generates invoked event
        let generateEventHandler mi  handlerName =
            let add = defineMethod typeBuilder mi
            let il = add.GetILGenerator()
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldfld, invokedField)
            let get_Publish = typeof<Event<EventHandler,EventArgs>>.GetProperty("Publish").GetGetMethod()
            il.Emit(OpCodes.Callvirt, get_Publish)
            il.Emit(OpCodes.Ldarg_1)
            let handler = typeof<IDelegateEvent<EventHandler>>.GetMethod(handlerName)
            il.Emit(OpCodes.Callvirt, handler)
            il.Emit(OpCodes.Ret)
        // Generate IMockObject.Invoked event
        let invoked = typeof<IMockObject>.GetEvent("Invoked")
        generateEventHandler (invoked.GetAddMethod()) "AddHandler"
        generateEventHandler (invoked.GetRemoveMethod()) "RemoveHandler"
        /// Method overloads grouped by type
        let groupedMethods = calls |> Seq.groupBy fst
        /// Method argument lookup
        let argsLookup = ResizeArray<obj[]>()
        /// Method return values
        let returnValues = ResizeArray<obj>()
        /// Abstract type's methods including interfaces
        let abstractMethods = seq {
            let attr = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance
            yield! abstractType.GetMethods(attr) |> Seq.filter (fun mi -> mi.IsVirtual && (not mi.IsFinal))
            (*for interfaceType in abstractType.GetInterfaces() do
                yield! interfaceType.GetMethods() *)
            }
        /// Generates a default value
        let generateDefaultValueReturn (il:ILGenerator) (returnType:Type) =
            let x = il.DeclareLocal(returnType)
            il.Emit(OpCodes.Ldloca_S, x.LocalIndex)
            il.Emit(OpCodes.Initobj, returnType)
            il.Emit(OpCodes.Ldloc, x.LocalIndex)
            il.Emit(OpCodes.Ret)
        // Implement abstract type's methods
        for abstractMethod in abstractMethods do
            /// Method builder
            let methodBuilder = defineMethod typeBuilder abstractMethod
            /// IL generator
            let il = methodBuilder.GetILGenerator()
            // Add invocation
            generateAddInvocation il invocationsField abstractMethod
            // Trigger invoked event
            generateTrigger il invokedField
            let definition (m:MethodInfo) =
                if m.IsGenericMethod then m.GetGenericMethodDefinition() else m
            /// Method overloads defined for current method
            let overloads = groupedMethods |> Seq.tryFind (fst >> definition >> (=) abstractMethod)
            match overloads with
            | Some (_, overloads) ->
                let toOverload = generateOverload il (argsLookup,argsField) (returnValues,returnValuesField)
                overloads |> Seq.toList |> List.rev |> Seq.iter toOverload
            | None -> ()
            if abstractMethod.ReturnType = typeof<System.Void> || abstractMethod.ReturnType = typeof<unit>
            then il.Emit(OpCodes.Ret)
            elif isStrict 
            then il.ThrowException(typeof<NotImplementedException>)
            else generateDefaultValueReturn il abstractMethod.ReturnType
            if abstractType.IsInterface then
                typeBuilder.DefineMethodOverride(methodBuilder, abstractMethod)
        /// Mock type
        let mockType = typeBuilder.CreateType()
        // Generate object instance
        let args = [|box (returnValues.ToArray());box (argsLookup.ToArray()); box args|]
        Activator.CreateInstance(mockType, args)

/// Mock mode
type MockMode = Strict = 0 | Loose = 1

/// Wildcard attribute
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property)>]
type WildcardAttribute() = inherit Attribute()

/// Predicate attribute
[<AttributeUsage(AttributeTargets.Method)>]
type PredicateAttribute() = inherit Attribute()

/// Returns attribute
[<AttributeUsage(AttributeTargets.Method)>]
type ReturnsAttribute() = inherit Attribute()

/// Raises attribute
[<AttributeUsage(AttributeTargets.Method)>]
type RaisesAttribute() = inherit Attribute()

open Emit
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

module private QuotationEvaluation =
    let rec eval (env:(string * obj) list) = function
        | Value(v,t) -> v
        | Var(x) -> (env |> List.find (fst >> (=) x.Name)) |> snd
        | Coerce(e,t) -> eval env e
        | NewObject(ci,args) -> ci.Invoke(evalAll env args)
        | NewArray(t,args) ->
            let array = Array.CreateInstance(t, args.Length) 
            args |> List.iteri (fun i arg -> array.SetValue(eval env arg, i))
            box array
        | NewUnionCase(case,args) -> FSharpValue.MakeUnion(case, evalAll env args)
        | NewRecord(t,args) -> FSharpValue.MakeRecord(t, evalAll env args)
        | NewTuple(args) ->
            let t = FSharpType.MakeTupleType [|for arg in args -> arg.Type|]
            FSharpValue.MakeTuple(evalAll env args, t)
        | TupleGet(tuple, index) -> FSharpValue.GetTupleField(eval env tuple, index)
        | FieldGet(None,fi) -> fi.GetValue(null)
        | FieldGet(Some(x),fi) -> fi.GetValue(eval env x)
        | PropertyGet(None, pi, args) -> pi.GetValue(null, evalAll env args)
        | PropertyGet(Some(x),pi,args) -> pi.GetValue(eval env x, evalAll env args)
        | Call(None,mi,args) -> mi.Invoke(null, evalAll env args)
        | Call(Some(x),mi,args) -> mi.Invoke(eval env x, evalAll env args)
        | Lambda(var,body) ->
            let ft = FSharpType.MakeFunctionType(var.Type, body.Type)
            FSharpValue.MakeFunction(ft, fun arg -> eval ((var.Name,arg)::env) body)
        | Application(lambda, arg) ->
            let lambda = eval env lambda
            let flags = BindingFlags.Instance ||| BindingFlags.Public
            let mi = lambda.GetType().GetMethod("Invoke", flags, null, [|arg.Type|], null)
            let arg = eval env arg
            mi.Invoke(lambda, [|arg|])
        | Let(var, assignment, body) ->
            let env = (var.Name, eval env assignment)::env
            eval env body
        | Sequential(lhs,rhs) -> eval env lhs |> ignore; eval env rhs
        | IfThenElse(condition, t, f) ->
            if eval env condition |> unbox then eval env t else eval env f
        | UnionCaseTest(t,info) -> 
            let target = eval env t
            let case, _ = FSharpValue.GetUnionFields(target, info.DeclaringType)
            case.Tag = info.Tag |> box
        | TypeTest(v,t) -> t.IsAssignableFrom((eval env v).GetType()) |> box
        | arg -> raise <| NotSupportedException(arg.ToString())
    and evalAll env args = [|for arg in args -> eval env arg|]

module Eval =
#if POWERPACK // F# PowerPack dependency
    open Microsoft.FSharp.Linq.QuotationEvaluation
    let eval (expr:Expr) = expr.EvalUntyped()
#else
    open QuotationEvaluation
    let eval expr = eval [] expr
#endif

module private Reflection =
    open Eval
    /// Returns true if method has specified attribute
    let hasAttribute a (mi:MethodInfo) = mi.GetCustomAttributes(a, true).Length > 0
    /// Converts expression to a tuple of MethodInfo and Arg array
    let toArgs args =
        [|for arg in args ->
            match arg with
            | Call(_, mi, _) when hasAttribute typeof<WildcardAttribute> mi -> Any
            | Call(_, mi, [pred]) when hasAttribute typeof<PredicateAttribute> mi -> Pred(eval pred)
            | expr -> eval expr |> Arg |]
    /// Active pattern matches method call expressions
    let (|MethodCall|_|) expr =
        let areEqual args vars =
            let eq = function Var(arg),var -> arg = var | _ -> false
            vars |> List.rev |> List.zip args |> List.forall eq
        let rec traverse vars = function
            | Let(var,TupleGet(_,n),e) when n = List.length vars -> traverse (var::vars) e
            | Call(Some(x),mi,args) when vars.Length = args.Length && areEqual args vars -> 
                Some(x,mi,[|for arg in args -> Any|])
            | _ -> None
        traverse [] expr
    /// Converts expression to a tuple of Expression, MethodInfo and Arg array
    let toCall = function
        | Lambda(unitVar,Call(Some(x),mi,[])) -> x, mi, [||]
        | Lambda(a,Call(Some(x),mi,[Var(a')])) when a=a' -> x, mi, [|Any|]
        | Lambda(_,MethodCall(x,mi,args)) -> x, mi, args
        | Call(Some(x), mi, args) -> x, mi, toArgs args
        | PropertyGet(Some(x), pi, args) -> x, pi.GetGetMethod(), toArgs args
        | PropertySet(Some(x), pi, args, value) -> x, pi.GetSetMethod(), toArgs [yield! args;yield value]
        | expr -> raise <| NotSupportedException(expr.ToString())
    /// Converts expression to a tuple of MethodInfo and Arg array
    let toCallOf abstractType expr =
        match toCall expr with
        | x, mi, args when x.Type = abstractType -> mi, args
        | _ -> raise <| NotSupportedException(expr.ToString())   
    /// Converts expression to a tuple of MethodInfo, Arg array and Result
    let rec toCallResult = function
        | ForIntegerRangeLoop(v,a,b,y) -> [for i = eval a :?> int to eval b :?> int do yield! toCallResult y]
        | Sequential(x,y) -> toCallResult x @ toCallResult y
        | Call(None, mi, [lhs;rhs]) when hasAttribute typeof<ReturnsAttribute> mi -> 
            let x, mi, args = toCall lhs
            let returns = ReturnValue(eval rhs, mi.ReturnType)
            [x, mi,(args,returns)]
        | Call(None, mi, [lhs;rhs]) when hasAttribute typeof<RaisesAttribute> mi -> 
            let x, mi, args = toCall lhs
            let raises = RaiseValue(eval rhs :?> exn)
            [x, mi,(args,raises)]
        | Call(Some(x), mi, args) when mi.ReturnType = typeof<unit> || mi.ReturnType = typeof<Void> ->
            [x, mi,(toArgs args,Unit)]
        | PropertySet(Some(x), pi, args, value) ->
            [x, pi.GetSetMethod(),(toArgs [yield! args; yield value], Unit)]
        | expr -> invalidOp(expr.ToString())
    let rec toCallResultOf abstractType expr =
        let calls = toCallResult expr
        [for (x,mi,(arg,result)) in calls -> 
            if x.Type = abstractType then mi,(arg,result)
            else raise <| NotSupportedException(expr.ToString())]
    /// Converts expression to corresponding event Add and Remove handlers
    let toHandlers abstractType = function
        | Call(None, mi, [Lambda(_,Call(Some(x),addHandler,_));
                          Lambda(_,Call(Some(_),removeHandler,_));_]) 
                          when x.Type = abstractType -> 
            addHandler, removeHandler
        | expr -> raise <| NotSupportedException(expr.ToString())

open Reflection

/// Generic mock type over abstract types and interfaces
type Mock<'TAbstract when 'TAbstract : not struct> internal (mode,calls) =
    /// Abstract type
    let abstractType = typeof<'TAbstract>
    /// Constructs mock builder
    new () = Mock(MockMode.Loose,[])
    new (mode) = Mock(mode,[])
    /// Specifies a method or property of the abstract type as a quotation
    member this.Setup(f:'TAbstract -> Expr<'TReturnValue>) =
        let default' = Unchecked.defaultof<'TAbstract>
        let call = toCallOf abstractType (f default')
        ResultBuilder<'TAbstract,'TReturnValue>(mode,call,calls)
    /// Specifies a method or property of the abstract type by name
    member this.SetupByName<'TReturnValue>(name) =
        let attr = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance
        let mi = typeof<'TAbstract>.GetMethod(name, attr)
        let args = [|for arg in mi.GetParameters() -> Any|] 
        ResultBuilder<'TAbstract,'TReturnValue>(mode,(mi,args),calls)
    /// Specifies an event of the abstract type as a quotation
    member this.SetupEvent(f:'TAbstract -> Expr<'TEvent>) =
        let default' = Unchecked.defaultof<'TAbstract>
        let handlers = toHandlers abstractType (f default')
        EventBuilder<'TAbstract,'TEvent>(mode,handlers,calls)
    /// Specifies an event of the abstract type as a quotation
    member this.SetupMethod(f:'TAbstract -> Expr<'TArgs -> 'TReturnValue>) =
        let default' = Unchecked.defaultof<'TAbstract>
        let call = toCallOf abstractType (f default')
        ResultBuilder<'TAbstract,'TReturnValue>(mode,call,calls)
    /// Creates a mocked instance of the abstract type
    member this.Create() = mock(MockMode.Strict = mode, typeof<'TAbstract>, calls, [||]) :?> 'TAbstract
    /// Creates a mocked instance of a class using the specified constructor arguments
    member this.Create([<ParamArray>] args:obj[]) = 
        mock(MockMode.Strict = mode, typeof<'TAbstract>, calls, args) :?> 'TAbstract
    /// Creates a boxed instance of the abstract type
    static member Create(abstractType:Type) = mock(false, abstractType, [], [||])
    /// Creates a mocked instance of the abstract type
    static member With(f:'TAbstract -> Expr<_>) =
        let default' = Unchecked.defaultof<'TAbstract>
        let calls = toCallResultOf typeof<'TAbstract> (f default')
        Mock<'TAbstract>(MockMode.Loose, calls).Create()
    /// Specifies a mock of a type with a given method
    static member Method(f:'TAbstract -> Expr<'TArgs -> 'TReturnValue>) =
        let default' = Unchecked.defaultof<'TAbstract>
        let call = toCallOf typeof<'TAbstract> (f default')
        ReturnBuilder<'TAbstract,'TReturnValue>(call)
    /// Specifies a mock of a type with a given property
    static member Property(f:'TAbstract -> Expr<'TReturnValue>) =
        let default' = Unchecked.defaultof<'TAbstract>
        let call = toCallOf typeof<'TAbstract> (f default')
        ReturnBuilder<'TAbstract,'TReturnValue>(call)
/// Generic builder for specifying method result
and ReturnBuilder<'TAbstract,'TReturnValue when 'TAbstract : not struct> 
    internal (call) =
    let mi, args = call
    /// Specifies the return value of a method or property
    member this.Returns(value:'TReturnValue) =
        let result = 
            if typeof<'TReturnValue> = typeof<unit> then Unit 
            else ReturnValue(value,typeof<'TReturnValue>)
        mock(false,typeof<'TAbstract>,[(mi, (args, result))],[||]) :?> 'TAbstract
    /// Specifies a computed return value of a method or property
    member this.Returns(f:unit -> 'TReturnVaue) =
        mock(false,typeof<'TAbstract>,[(mi, (args, ReturnFunc(f)))],[||]) :?> 'TAbstract
/// Generic builder for specifying method or property results
and ResultBuilder<'TAbstract,'TReturnValue when 'TAbstract : not struct> 
    internal (mode, call, calls) =
    let mi, args = call
    /// Specifies the return value of a method or property
    member this.Returns(value:'TReturnValue) =
        let result = 
            if typeof<'TReturnValue> = typeof<unit> then Unit 
            else ReturnValue(value,typeof<'TReturnValue>)
        Mock<'TAbstract>(mode,(mi, (args, result))::calls)
    /// Specifies a computed return value of a method or property
    member this.Returns(f:unit -> 'TReturnVaue) =
        Mock<'TAbstract>(mode,(mi, (args, ReturnFunc(f)))::calls)
    /// Calls the specified function to compute the return value
    [<RequiresExplicitTypeArguments>]
    member this.Calls<'TArgs>(f:'TArgs -> 'TReturnValue) =
        Mock<'TAbstract>(mode,(mi, (args, Call(f)))::calls)
    /// Specifies the exception a method or property raises
    [<RequiresExplicitTypeArguments>]
    member this.Raises<'TException when 'TException : (new : unit -> 'TException) 
                                   and  'TException :> exn>() =
        Mock<'TAbstract>(mode,(mi, (args, Raise(typeof<'TException>)))::calls)
    /// Specifies the exception value a method or property raises
    member this.Raises(exnValue:exn) =
        Mock<'TAbstract>(mode,(mi, (args, RaiseValue(exnValue)))::calls)
/// Generic builder for specifying event values
and EventBuilder<'TAbstract,'TEvent when 'TAbstract : not struct> 
    internal (mode, handlers, calls) =
    let add, remove = handlers
    /// Specifies the published event value
    member this.Publishes(value:'TEvent) =
        Mock<'TAbstract>(mode, 
                         (add, ([|Any|], Handler("AddHandler",value)))::
                         (remove, ([|Any|], Handler("RemoveHandler",value)))::
                         calls)

type Mock =
    /// Creates a mocked instance of the abstract type
    static member Of<'TAbstractType>() = 
        mock(false, typeof<'TAbstractType>, [], [||]) :?> 'TAbstractType
    static member Of<'TAbstractType>(args) =
        mock(false, typeof<'TAbstractType>, [], args) :?> 'TAbstractType
/// Specifies valid invocation count
type Times internal (predicate:int -> bool) =
    member __.Match(n) = predicate(n)
    static member Exactly(n:int) = Times((=) n)
    static member AtLeast(n:int) = Times((<=) n)
    static member AtLeastOnce = Times.AtLeast(1)
    static member AtMost(n:int) = Times((>=) n)
    static member AtMostOnce = Times.AtMost(1)
    static member Never = Times((=) 0)
    static member Once = Times((=) 1)

[<AutoOpen;CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Times =
    let exactly = Times.Exactly
    let atleast = Times.AtLeast
    let atleastonce = Times.AtLeastOnce
    let atmost = Times.AtMost
    let atmostonce = Times.AtMostOnce
    let never = Times.Never
    let once = Times.Once

module internal Verification =
    /// Return true if methods match
    let methodsMatch (a:MethodBase) (b:MethodBase) =
        a.Name = b.Name &&
        a.GetParameters().Length = b.GetParameters().Length &&
        Array.zip (a.GetParameters()) (b.GetParameters())
        |> Array.forall (fun (a,b) -> a.ParameterType = b.ParameterType)
    /// Returns true if arguments match
    let argsMatch argType expectedArg actualValue =
        match expectedArg with
        | Any -> true
        | Arg(expected) -> obj.Equals(expected,actualValue)
        | OutArg(_) -> true
        | Pred(p) ->
            let f = FSharpType.MakeFunctionType(argType,typeof<bool>).GetMethod("Invoke")
            f.Invoke(p,[|actualValue|]) :?> bool
        | PredUntyped(p) -> raise <| NotSupportedException()
    let invokeMatch (expectedMethod:MethodBase) (expectedArgs:Arg[]) (actual:Invocation) =
        let ps = expectedMethod.GetParameters()
        methodsMatch expectedMethod actual.Method &&
        Array.zip expectedArgs actual.Args
        |> Array.mapi (fun i (e,a) -> ps.[i].ParameterType,e,a)
        |> Array.forall (fun (t,e,a) -> argsMatch t e a)
    /// Returns invocation count matching specificed expression
    let countInvocations (mock:IMockObject) (expectedMethod) (expectedArgs) =
        mock.Invocations
        |> Seq.filter (invokeMatch expectedMethod expectedArgs)
        |> Seq.length
    let getMock (x:obj) =
        match x with
        | :? IMockObject as mock -> mock
        | _ -> failwith "Object instance is not a mock"

open Eval
open Verification

[<AutoOpen>]
module private Format =
    let invoke (mi:MethodBase,args:obj seq) =
        let args = args |> Seq.map (sprintf "%O")
        mi.Name + "(" + (String.concat "," args) + ")"
    let expected (mi:MethodBase,args:Arg[]) =
        let args = args |> Seq.map (function Arg x -> x | _ -> box "_") 
        invoke (mi, args)
    let unexpected (expectedMethod,expectedArgs,invocation:Invocation) =
        "Unexpected member invocation\r\n" +
        "Expected: " + expected(expectedMethod,expectedArgs) + "\r\n" +
        "Actual: " + invoke(invocation.Method,invocation.Args)

type Mock with
    /// Verifies expected call count against instance member invocations on specified mock
    static member Verify(expr:Expr, expectedTimes:Times) =
        let target,expectedMethod,expectedArgs = toCall expr
        let mock = target |> eval |> getMock
        let actualCalls = countInvocations mock expectedMethod expectedArgs
        if not <| expectedTimes.Match(actualCalls) then 
            failwith <| expected(expectedMethod,expectedArgs)
    /// Verifies expression was invoked at least once
    static member Verify(expr:Expr) = Mock.Verify(expr, atleastonce)
    /// Verifies expected expression call count on invocation
    static member Expect(expr:Expr, expectedTimes:Times) =
        let target,expectedMethod,expectedArgs = toCall expr
        let mock = target |> eval |> getMock
        let verify () =
            let actualCalls = countInvocations mock expectedMethod expectedArgs
            if not <| expectedTimes.Match(actualCalls) then 
                failwith <| expected(expectedMethod,expectedArgs)
        mock.Invoked.Subscribe(fun _ -> 
            let last = mock.Invocations.[mock.Invocations.Count-1]
            if invokeMatch expectedMethod expectedArgs last then verify()
            ) |> ignore
        mock.Verifiers.Add(Action(verify))
    // Verify call sequence in order
    static member VerifySequence(expr:Expr) =
        let calls = toCallResult expr
        let mocks = System.Collections.Generic.Dictionary()
        for target, expectedMethod,(expectedArgs,result) in calls do
            let mock = eval target |> getMock
            if not <| mocks.ContainsKey mock then mocks.Add(mock,0)
            let n = mocks.[mock]
            if mock.Invocations.Count = n then
                failwith <| "Missing expected member invocation: " + expected(expectedMethod,expectedArgs)
            let actual = mock.Invocations.[n]
            if not <| invokeMatch expectedMethod expectedArgs actual then
                failwith  <| unexpected(expectedMethod,expectedArgs,actual)
            mocks.[mock] <- n + 1
        for pair in mocks do
            let mock, n = pair.Key, pair.Value 
            if mock.Invocations.Count > n then 
                let last = mock.Invocations.[n]
                failwith <| "Unexpected member invocation: " + invoke(last.Method, last.Args)
    /// Verifies all expectations
    static member VerifyAll(mock:obj) =
        let mock = mock :?> IMockObject
        for verify in mock.Verifiers do verify.Invoke()

[<AutoOpen>]
module internal Expectations =
    let setup (mock:IMockObject) (calls:(#MethodBase * Arg[]) list) =
        let index = ref 0
        mock.Invoked.Subscribe (fun _ ->
            let last = mock.Invocations.[mock.Invocations.Count-1]
            if !index = calls.Length then 
                failwith <| "Unexpected member invocation: " + invoke(last.Method, last.Args)
            let expected = calls.[!index]
            incr index
            let expectedMethod, expectedArgs = expected
            if not <| invokeMatch expectedMethod expectedArgs last then
                failwith <| unexpected(expectedMethod,expectedArgs,last)
        ) |> ignore
        let verify () = 
            if !index < calls.Length then
                let mi, args = calls.[!index]
                failwith <| "Missing expected member invocation: " + expected(mi, args)
        mock.Verifiers.Add(Action(verify))

type Mock<'TAbstract> with
    /// Verifies expected expression sequence
    static member ExpectSequence(f:'TAbstract -> Expr<_>) =
        let default' = Unchecked.defaultof<'TAbstract>
        let calls = toCallResultOf typeof<'TAbstract> (f default')
        let mockObject = mock(false, typeof<'TAbstract>, calls, [||]) 
        let mock = mockObject :?> IMockObject
        let calls = calls |> List.map (fun (mi,(args,_)) -> mi,args)
        setup mock calls
        mockObject :?> 'TAbstract

type [<Sealed>] It private () =
    /// Marks argument as matching any value
    [<Wildcard>] static member IsAny<'TArg>() = Unchecked.defaultof<'TArg>
    /// Marks argument as matching specific values
    [<Predicate>] static member Is<'TArg>(f:'TArg -> bool) = Unchecked.defaultof<'TArg>

[<AutoOpen;CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module It =
    /// Marks argument as matching any value
    let [<Wildcard>] inline any () : 'TArg = It.IsAny()
    /// Marks argument as matching specific values
    let [<Predicate>] inline is (f) : 'TArg = It.Is(f)

[<AutoOpen>]
module Operators =
    /// Signifies source expression returns specified value
    let [<Returns>] (-->) (source:'T) (value:'T) = ()
    /// Signifies source expression raises specified exception
    let [<Raises>] (==>) (source:'T) (value:exn) = ()
    /// Returns a mock of the required type
    let mock() : 'TAbstractType = Mock.Of<'TAbstractType>()
    /// Verifies the expression occurs the specified number of times
    let verify expr times = Mock.Verify(expr, times)
    /// Expects the expression occurs the specified number of times
    let expect expr times = Mock.Expect(expr, times)
    /// Verifies all expectations set on the specified mock object
    let verifyAll mock = Mock.VerifyAll mock
    /// Verifies an expression sequence has occured
    let verifySeq expr = Mock.VerifySequence expr
    /// Expects the expression sequence to occur
    let expectSeq expr = Mock.ExpectSequence expr
