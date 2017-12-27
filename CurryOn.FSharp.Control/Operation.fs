﻿namespace FSharp.Control

open FSharp.Reflection
open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

/// Represents the successful result of an Operation that also yields events
/// such as warnings, informational messages, or other domain events
[<Struct>]
type SuccessfulResultWithEvents<'result,'event> =
    {
        Value: 'result
        Events: 'event list
    }

/// Represents the successful result of an Operation,
/// which can be either a value or a value with a list of events
[<Struct>]
type SuccessfulResult<'result,'event> =
    | Value of ResultValue: 'result
    | WithEvents of ResultWithEvents: SuccessfulResultWithEvents<'result,'event>
    member this.Result =
        match this with
        | Value value -> value
        | WithEvents withEvents -> withEvents.Value
    member this.Events =
        match this with
        | Value _ -> []
        | WithEvents withEvents -> withEvents.Events    

/// Represents the result of a completed operation,
/// which can be either a Success or a Failure
[<Struct>]
type OperationResult<'result,'event> =
    | Success of Result: SuccessfulResult<'result,'event>
    | Failure of ErrorList: 'event list
     /// Creates a Failure result with the given events.
    static member FailWith(events: 'event seq) : OperationResult<'result, 'event> = 
        OperationResult<'result, 'event>.Failure(events |> Seq.toList)
    /// Creates a Failure result with the given event.
    static member FailWith(event: 'event) : OperationResult<'result, 'event> = 
        OperationResult<'result, 'event>.Failure([event])    
    /// Creates a Successful result with the given value.
    static member Succeed(value: 'result) : OperationResult<'result, 'event> =         
        OperationResult<'result, 'event>.Success(Value value)
    /// Creates a Successful result with the given value and the given event.
    static member Succeed(value: 'result, event: 'event) : OperationResult<'result, 'event> = 
        OperationResult<'result, 'event>.Success(WithEvents {Value = value; Events = [event]})
    /// Creates a Successful result with the given value and the given events.
    static member Succeed(value:'result, events: 'event seq) : OperationResult<'result, 'event> = 
        OperationResult<'result, 'event>.Success(WithEvents {Value = value; Events = events |> Seq.toList})
    /// The list of events for the Operation Result, whether success or failure
    member this.Events =
        match this with
        | Success successfulResult -> successfulResult.Events
        | Failure events -> events
    override this.ToString () =
        match this with
        | Success successfulResult -> sprintf "OK: %A - %s" successfulResult.Result (String.Join(Environment.NewLine, successfulResult.Events |> Seq.map (fun x -> x.ToString())))
        | Failure errors -> sprintf "Error: %s" (String.Join(Environment.NewLine, errors |> Seq.map (fun x -> x.ToString())))    

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Result =
    type UnionCaseInfo with member this.Fields = this.GetFields()

    /// Creates a successful OperationResult with the given value
    let success<'result, 'event> : 'result -> OperationResult<'result,'event> = fun value -> value |> Value |> Success

    /// Creates a successful OperationResult with the given value and events
    let successWithEvents value events = {Value = value; Events = events} |> WithEvents |> Success

    /// Creates a failed OperationResult with the given error events
    let failure<'result,'event> : 'event list -> OperationResult<'result,'event> = fun events -> events |> Failure

    /// Unwraps an AggregateException if there is only 1 inner exception
    let unwrapAggregateException (aggregate: AggregateException) =
        if aggregate.InnerExceptions.Count = 1
        then aggregate.InnerExceptions |> Seq.head
        else aggregate :> exn

    /// Converts a System.Exception to an instance of the 'event type and then creates a Failure OperationResult with that event
    let inline convertExceptionToResult<'result,'event> (except: exn) =
        let ex = 
            match except with
            | :? AggregateException as agg -> unwrapAggregateException agg
            | _ -> except
        let union = FSharpType.GetUnionCases(typeof<OperationResult<'result, 'event>>)
        if typeof<'event>.IsAssignableFrom(typeof<exn>)
        then FSharpValue.MakeUnion(union.[1], [|[ex] |> box|]) |> unbox<OperationResult<'result, 'event>>
        elif FSharpType.IsUnion typeof<'event>
        then let cases = FSharpType.GetUnionCases(typeof<'event>)
             match cases |> Seq.tryFind (fun case -> case.Fields.Length = 1 && case.Fields.[0].PropertyType.IsAssignableFrom(typeof<exn>)) with
             | Some case -> FSharpValue.MakeUnion(union.[1], [|[FSharpValue.MakeUnion(case, [|ex |> box|]) |> unbox<'event>] |> box|]) |> unbox<OperationResult<'result, 'event>>
             | None -> failwithf "No Union Case of Event Type %s Supports Construction from an Unhandled Exception: \r\n%O" typeof<'event>.Name ex
        else failwithf "Unable To Construct a Failure of type %s from Unhandled Exception: \r\n%O" typeof<'event>.Name ex

    /// Merge the results and the domain events of two OperationResults
    let inline merge (result1: OperationResult<unit,'event>) (result2: OperationResult<'result,'event>) =
        match result1 with
        | Success successfulResult1 ->
            match successfulResult1 with
            | Value value1 -> 
                match result2 with
                | Success successfulResult2 -> 
                    match successfulResult2 with
                    | Value value2 -> success (value1,value2)
                    | WithEvents withEvents2 -> successWithEvents (value1,withEvents2.Value) withEvents2.Events
                | Failure events2 -> Failure events2
            | WithEvents withEvents1 ->
                match result2 with
                | Success successfulResult2 -> 
                    match successfulResult2 with
                    | Value value2 -> successWithEvents (withEvents1.Value,value2) withEvents1.Events
                    | WithEvents withEvents2 -> successWithEvents (withEvents1.Value,withEvents2.Value) (withEvents1.Events@withEvents2.Events)
                | Failure events2 -> Failure (withEvents1.Events@events2)
        | Failure events1 -> 
            match result2 with
            | Success successfulResult2 ->
                match successfulResult2 with
                | Value value -> Failure events1
                | WithEvents withEvents2 -> Failure (events1@withEvents2.Events)
            | Failure events2 -> Failure (events1@events2)   

    /// Merges the given results into the existing OperationResult
    let inline mergeEvents result events =
        match result with
        | Success successfulResult ->
            match successfulResult with
            | Value value -> successWithEvents value events
            | WithEvents withEvents -> successWithEvents withEvents.Value (events@withEvents.Events)
        | Failure errorEvents ->
            failure (events@errorEvents)

    /// Returns true if the OperationResult was not successful.
    let inline failed result = 
        match result with
        | Failure _ -> true
        | _ -> false

    /// Returns true if the OperationResult was succesful.
    let inline ok result =
        match result with
        | Success _ -> true
        | _ -> false

    /// Takes an OperationResult and maps it with fSuccess if it is a Success otherwise it maps it with fFailure.
    let inline either fSuccess fFailure operationResult = 
        match operationResult with
        | Success successfulResult -> 
            match successfulResult with
            | Value value -> fSuccess (value, [])
            | WithEvents withEvents -> fSuccess (withEvents.Value, withEvents.Events)
        | Failure events -> fFailure events

    /// If the given OperationResult is a Success the wrapped value will be returned. 
    /// Otherwise the function throws an exception with the Failure message of the result.
    let inline returnOrFail result = 
        let inline raiseExn events = 
            events
            |> Seq.map (sprintf "%O")
            |> String.concat (sprintf "%s\t" Environment.NewLine)
            |> failwith
        either fst raiseExn result

    /// If the OperationResult is a Success it executes the given function on the value.
    /// Otherwise the exisiting failure is propagated.
    let inline bind f result = 
        let inline fSuccess (x, events) = mergeEvents (f x) events
        let inline fFailure (events) = Failure events
        either fSuccess fFailure result

    /// Flattens a nested OperationResult given the Failure types are equal
    let inline flatten (result : OperationResult<OperationResult<_,_>,_>) =
        result |> bind id

    /// If the OperationResult is a Success it executes the given function on the value. 
    /// Otherwise the exisiting failure is propagated.
    /// This is the infix operator version of the bind function
    let inline (>>=) result f = bind f result

    /// If the wrapped function is a success and the given result is a success the function is applied on the value. 
    /// Otherwise the exisiting error events are propagated.
    let inline apply wrappedFunction result = 
        match wrappedFunction, result with
        | Success successfulResult1, Success successfulResult2 -> Success(WithEvents {Value = successfulResult1.Result successfulResult2.Result; Events = successfulResult1.Events @ successfulResult2.Events})
        | Failure errors, Success _ -> Failure errors
        | Success _, Failure errors -> Failure errors
        | Failure errors1, Failure errors2 -> Failure <| errors1 @ errors2

    /// If the wrapped function is a success and the given result is a success the function is applied on the value. 
    /// Otherwise the exisiting error messages are propagated.
    /// This is the infix operator version of the apply function
    let inline (<*>) wrappedFunction result = apply wrappedFunction result

    /// Lifts a function into an OperationResult container and applies it on the given result.
    let inline lift f result = apply (Success <| Value f) result

    /// Maps a function over the existing error events in case of failure. In case of success, the message type will be changed and warnings will be discarded.
    let inline mapFailure f result =
        match result with
        | Success successfulResult -> Success (Value successfulResult.Result)
        | Failure errors -> Failure <| f errors

    /// Lifts a function into a Result and applies it on the given result.
    /// This is the infix operator version of the lift function
    let inline (<!>) f result = lift f result

    /// Promote a function to a monad/applicative, scanning the monadic/applicative arguments from left to right.
    let inline lift2 f a b = f <!> a <*> b

    /// If the OperationResult is a Success it executes the given success function on the value and the events.
    /// If the OperationResult is a Failure it executes the given failure function on the events.
    /// Result is propagated unchanged.
    let inline eitherTee fSuccess fFailure result =
        let inline tee f x = f x; x;
        tee (either fSuccess fFailure) result

    /// If the OperationResult is a Success it executes the given function on the value and the events.
    /// Result is propagated unchanged.
    let inline successTee f result = 
        eitherTee f ignore result

    /// If the OperationResult is a Failure it executes the given function on the events.
    /// Result is propagated unchanged.
    let inline failureTee f result = 
        eitherTee ignore f result

    /// Collects a sequence of OperationResults and accumulates their values.
    /// If the sequence contains an error the error will be propagated.
    let inline collect xs = 
        Seq.fold (fun result next -> 
            match result, next with
            | Success sr1, Success sr2 -> Success <| WithEvents {Value = sr2.Result :: sr1.Result; Events = sr1.Events @ sr2.Events}
            | Success sr1, Failure events2 -> Failure <| sr1.Events @ events2
            | Failure events1, Success sr2 -> Failure <| events1 @ sr2.Events
            | Failure events1, Failure events2 -> Failure (events1 @ events2)) (Success <| Value []) xs
        |> lift List.rev

    /// Converts an option into an OperationResult, using the provided events if None.
    let inline ofOptionWithEvents opt noneEvents = 
        match opt with
        | Some x -> Success <| Value x
        | None -> Failure noneEvents

    /// Converts an option into an OperationResult, using the provided event if None.
    let inline ofOptionWithEvent opt noneEvent = ofOptionWithEvents opt [noneEvent]

    /// Converts an option into an OperationResult.
    let inline ofOption opt = ofOptionWithEvents opt []

    /// Converts a Choice into an OperationResult.
    let inline ofChoice choice =
        match choice with
        | Choice1Of2 v -> Success <| Value v
        | Choice2Of2 e -> Failure [e]

    /// Converts a Choice with a List of Events in Choice2of2 into an OperationResult.
    let inline ofChoiceWithEvents choice =
        match choice with
        | Choice1Of2 v -> Success <| Value v
        | Choice2Of2 es -> Failure es

    /// Converts a Task<'a> to a Task<OperationResult<'a,'b>>
    let inline ofTask<'result,'event> (task: Task<'result>) =
        task.ContinueWith(fun (t: Task<'result>) -> 
            if t.IsFaulted
            then convertExceptionToResult<'result,'event> t.Exception
            elif t.IsCanceled
            then convertExceptionToResult <| OperationCanceledException()
            else t.Result |> success)

    /// Categorizes a result based on its state and the presence of extra messages
    let inline (|Pass|Warn|Fail|) result =
      match result with
      | Success successfulResult -> 
        match successfulResult with
        | Value value -> Pass value
        | WithEvents withEvents -> Warn (withEvents.Value, withEvents.Events)
      | Failure events -> Fail events

    /// Treat a succeessful result with warning events as a failure
    let inline failOnWarnings result =
      match result with
      | Warn (_,warnings) -> Failure warnings
      | _  -> result 

/// Represents an incomplete operation that is currently executing
[<Struct>]
type InProcessOperation<'result,'event> = 
    {
        Task: Task<'result>
        EventsSoFar: 'event list
    }

/// Represents an Operation composed of one or more steps,
/// which may be already completed, in-process, or cancelled
and [<Struct>] Operation<'result,'event> =
    | Completed of Result: OperationResult<'result,'event>
    | InProcess of IncompleteOperation: InProcessOperation<'result,'event>
    | Cancelled of EventsSoFar: 'event list
    member this.Events =
        match this with
        | Completed result -> result.Events
        | InProcess inProcess -> inProcess.EventsSoFar
        | Cancelled events -> events

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Operation =
    /// Creates a completed Operation with a successful OperationResult of the given value
    let inline success<'result, 'event> : 'result -> Operation<'result,'event> = fun value -> value |> Result.success<'result,'event> |> Completed

    /// Creates a completed Operation with a successful OperationResult of the given value and events
    let inline successWithEvents value events = Completed <| Result.successWithEvents value events

    /// Creates a failed Operation with the given errors in its OperationResult
    let inline failure<'result,'event> : 'event list -> Operation<'result,'event> = fun events -> events |> Result.failure<'result,'event> |> Completed

    /// Synchronously returns the operation of a result, waiting for it to complete if necessary
    let inline wait operation =
        match operation with
        | Completed result -> result
        | InProcess inProcess -> Result.ofTask(inProcess.Task).Result
        | Cancelled events -> Failure events

    /// Returns the operation of a result, asynchronously waiting for it to complete if necessary
    let inline waitAsync operation =
        async {
            match operation with
            | Completed result -> return result
            | InProcess inProcess ->
                try
                    let! result = inProcess.Task |> Async.AwaitTask
                    return Result.success result
                with | ex ->
                    return ex |> Result.convertExceptionToResult
            | Cancelled events -> return Failure events
        }

    /// Returns a Task<OperationResult<'result,'event>> representing the result of the operation when it completes
    let inline waitTask operation = operation |> (waitAsync >> Async.StartAsTask)

    /// Synchronously wait for an Operation to complete
    let inline complete operation =
        match operation with
        | Completed _ as completed -> completed 
        | InProcess _ as inProcess ->
            let result = wait inProcess
            Completed result
        | Cancelled events -> Completed <| Failure events

    /// Asynchronously wait for an Operation to complete
    let inline completeAsync operation =
        async {
            match operation with
            | Completed _ as completed -> return completed 
            | InProcess _ as inProcess ->
                let! result = waitAsync inProcess
                return Completed result
            | Cancelled events -> return Completed <| Failure events
        }

    /// Returns a Task<Operation<'result,'event>> representing the Completed Operation when it has finished executing
    let inline completeTask operation = operation |> (completeAsync >> Async.StartAsTask)

    /// Merges the given events into the existing operation
    let inline mergeEvents operation events =
        match operation with
        | Completed result -> Completed <| Result.mergeEvents result events
        | InProcess inProcess -> {inProcess with EventsSoFar = (events@inProcess.EventsSoFar)} |> InProcess
        | Cancelled eventsSoFar -> events @ eventsSoFar |> Cancelled

    /// Executes the given function and returns a completed Operation with either a SuccessfulResult or the thrown exception in a Failure
    let inline catch f x = try success (f x) with | ex -> failure [ex]

    /// Returns true if the Operation failed (not successful or cancelled)
    /// If the Operation is InProcess, it is waited on synchronously, then the same logic will apply
    let inline failed operation = 
        match operation with
        | Completed result -> Result.failed result       
        | InProcess _ as inProcess -> inProcess |> wait |> Result.failed
        | _ -> false

    /// Returns true if the Operation was cancelled (not successful, failure, or in process)
    let inline cancelled operation = 
        match operation with
        | Cancelled _ -> true
        | _ -> false

    /// Returns true if the Operation was succesful (not failure or cancelled)
    /// If the Operation is InProcess, it is waited on synchronously, then the same logic will apply
    let inline ok operation =
        match operation with
        | Completed result -> Result.ok result
        | InProcess _ as inProcess -> inProcess |> wait |> Result.ok
        | _ -> false

    /// If the given Operation is Completed and a Success the wrapped value will be returned. 
    /// If the given Operation is InProcess, the Operation will be waited on synchronously, then the same logic will apply
    /// Otherwise the function throws an exception with the Failure or Cancellation events of the result.
    let inline returnOrFail operation = 
        match operation with
        | Completed result -> result
        | InProcess _ as inProcess -> inProcess |> wait
        | Cancelled events -> events |> Failure
        |> Result.returnOrFail 

    /// Converts a System.Exception to an instance of the 'event type and then creates a Completed Failure Operation with that event
    let inline convertExceptionToResult<'result,'event> (except: exn) =
        Completed <| Result.convertExceptionToResult<'result,'event> except