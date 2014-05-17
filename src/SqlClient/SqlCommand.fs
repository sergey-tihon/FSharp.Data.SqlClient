﻿namespace FSharp.Data.SqlClient

open System
open System.Collections.Generic
open System.Data
open System.Data.SqlClient
open System.Dynamic
open System.Reflection
open System.Threading

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection
open Samples.FSharp.ProvidedTypes

open FSharp.Data.Internals

type NullsToOptionsMapper = obj[] -> obj[]

type ISqlCommand<'TResult> = 
    abstract AsyncExecute : parameters: (string * obj)[] -> Async<'TResult>
    abstract Execute : parameters: (string * obj)[] -> 'TResult
    abstract AsyncExecuteNonQuery : parameters: (string * obj)[] -> Async<int>
    abstract ExecuteNonQuery : parameters: (string * obj)[] -> int
    abstract ToTraceString : parameters: (string * obj)[] -> string

    abstract AsyncExecuteDataTable : parameters: (string * obj)[] -> Async<FSharp.Data.DataTable<DataRow>>
    abstract ExecuteDataTable : (string * obj)[] * NullsToOptionsMapper -> FSharp.Data.DataTable<DataRow>

type SqlCommand<'TResult> (connection, command, parameters, singleRow, 
                            mapper: CancellationToken option -> SqlDataReader -> 'TResult,
                            ?transaction : SqlTransaction) = 

    let cmd = new SqlCommand(command, connection)
    do 
        cmd.Parameters.AddRange( parameters)
        transaction |> Option.iter cmd.set_Transaction
            
    let behavior() =
        let connBehavior = 
            if cmd.Connection.State <> ConnectionState.Open then
                cmd.Connection.Open()
                CommandBehavior.CloseConnection
            else
                CommandBehavior.Default 
        connBehavior
        ||| (if singleRow then CommandBehavior.SingleRow else CommandBehavior.Default)
        ||| CommandBehavior.SingleResult

    let setParameters (parameters : (string * obj)[]) = 
        for name, value in parameters do
            
            let p = cmd.Parameters.[name]            

            if value = null 
            then 
                p.Value <- DbNull 
            else
                if not( p.SqlDbType = SqlDbType.Structured)
                then 
                    p.Value <- value
                else
                    let table : DataTable = unbox p.Value
                    table.Rows.Clear()
                    for rowValues in unbox<seq<obj[]>> value do
                        table.Rows.Add( rowValues) |> ignore

            if p.Value = DbNull 
            then 
                match p.SqlDbType with
                | SqlDbType.NVarChar -> p.Size <- 4000
                | SqlDbType.VarChar -> p.Size <- 8000
                | _ -> ()

    let executeReader parameters =  
        setParameters parameters      
        try 
            cmd.ExecuteReader(behavior())
        with _ ->
            cmd.Connection.Close()
            reraise()
    
    member this.ConnectionState () = cmd.Connection.State

    member this.AsSqlCommand () = 
        let clone = new SqlCommand(cmd.CommandText, new SqlConnection(cmd.Connection.ConnectionString), CommandType = cmd.CommandType)
        clone.Parameters.AddRange <| [| for p in cmd.Parameters -> SqlParameter(p.ParameterName, p.SqlDbType) |]
        clone

    interface ISqlCommand<'TResult> with

        member this.AsyncExecute parameters = 
            setParameters parameters 
            async {
                let! token = Async.CancellationToken                
                let! reader = 
                    try 
                        Async.FromBeginEnd((fun(callback, state) -> cmd.BeginExecuteReader(callback, state, behavior())), cmd.EndExecuteReader)
                    with _ ->
                        cmd.Connection.Close()
                        reraise()
                return mapper (Some token) reader
            }

        member this.Execute parameters = 
            setParameters parameters      
            let reader = 
                try 
                    cmd.ExecuteReader(behavior())
                with _ ->
                    cmd.Connection.Close()
                    reraise()
            mapper None reader
       
        member this.AsyncExecuteNonQuery parameters = 
            setParameters parameters  
            async {         
                use disposable = cmd.Connection.UseConnection()
                return! cmd.AsyncExecuteNonQuery() 
            }

        member this.ExecuteNonQuery parameters = 
            setParameters parameters  
            use disposable = cmd.Connection.UseConnection()
            cmd.ExecuteNonQuery() 
        
        member this.ToTraceString parameters =  
            setParameters parameters  //Dirty hack to make command figure out sizes. Won't hurt because all executes do the same
            let parameterDefinition (p : SqlParameter) =
                if p.Size <> 0 then
                    sprintf "%s %A(%d)" p.ParameterName p.SqlDbType p.Size
                else
                    sprintf "%s %A" p.ParameterName p.SqlDbType 
            seq {
              yield sprintf "exec sp_executesql N'%s'" cmd.CommandText
              
              yield cmd.Parameters
                    |> Seq.cast<SqlParameter> 
                    |> Seq.map parameterDefinition
                    |> String.concat ","
                    |> sprintf "N'%s'" 
              yield parameters
                    |> Seq.map(fun (name,value) -> sprintf "%s='%O'" name value) 
                    |> String.concat ","
            } |> String.concat "," //Using string.concat to handle annoying case with no parameters


        member this.AsyncExecuteDataTable parameters = 
            Unchecked.defaultof< Async<FSharp.Data.DataTable<DataRow>> >

        member this.ExecuteDataTable(parameters, nullToOptionMapper) = 
            use reader = executeReader parameters
            let result = new FSharp.Data.DataTable<DataRow>()
            result.Load(reader)
            result
            
    interface IDisposable with
        member this.Dispose() =
            cmd.Dispose()


type SqlCommandFactory private () =
    
    static member GetMethod(name, runtimeType) = 
        typeof<SqlCommandFactory>.GetMethod(name).MakeGenericMethod([| runtimeType |])
        
    static member ByConnectionString(connectionStringOrName, command, parameters, singleRow, mapper) = 
        let connectionStringName, isByName = Configuration.ParseConnectionStringName connectionStringOrName
        let runTimeConnectionString = 
            if isByName 
            then Configuration.GetConnectionStringRunTimeByName connectionStringName
            else connectionStringOrName
        new SqlCommand<_>(new SqlConnection(runTimeConnectionString), command, parameters, singleRow, mapper)  
   
    static member ByTransaction(transaction : SqlTransaction, command, parameters, singleRow, mapper) = 
        new SqlCommand<_>(transaction.Connection, command, parameters, singleRow, mapper, transaction) 
                
    static member GetDataTable(sqlDataReader : SqlDataReader) =
        use reader = sqlDataReader
        let result = new FSharp.Data.DataTable<DataRow>()
        result.Load(reader)
        result

    static member GetRecord(values : obj [], names : string []) = DynamicRecord((names, values) ||> Seq.zip |> dict)
                                    
    static member GetTypedSequence (mapNullables, rowMapper) =
        fun (token : CancellationToken option) (sqlDataReader : SqlDataReader) ->
        seq {
            try 
                while((token.IsNone || not token.Value.IsCancellationRequested) && sqlDataReader.Read()) do
                    let values = Array.zeroCreate sqlDataReader.FieldCount
                    sqlDataReader.GetValues(values) |> ignore
                    mapNullables values
                    yield rowMapper values
            finally
                sqlDataReader.Close()
        }
    
    static member SingeRow(mapNullables , rowMapper) =
        fun (_ : CancellationToken option) (sqlDataReader : SqlDataReader) ->
        try 
            if sqlDataReader.Read() then 
                let values = Array.zeroCreate sqlDataReader.FieldCount                
                sqlDataReader.GetValues(values) |> ignore
                if sqlDataReader.Read() then raise <| InvalidOperationException("Single row was expected.")
                mapNullables values
                Some <| rowMapper values
            else
                None                
        finally
                sqlDataReader.Close()
