(*** hide ***)
#r "../../bin/FSharp.Data.SqlClient.dll"

(**
Bridging the gap between F# types and T-SQL scripting
===================

SqlCommandProvider provides statically typed access to input parameters and result set of T-SQL command in idiomatic F# way.

<div class="row">
  <div class="span1"></div>
  <div class="span6">
    <div class="well well-small" id="nuget">
      The FSharp.Data.SqlClient library can be <a href="http://www.nuget.org/packages/FSharp.Data.SqlClient">installed from NuGet</a>:
      <pre>PM> Install-Package FSharp.Data.SqlClient</pre>
    </div>
  </div>
  <div class="span1"></div>
</div>

Sample code 
-------------------------------------

The query below retrieves top 3 sales representatives from North American region who have sales YTD of more than one million. 

*)

open FSharp.Data

[<Literal>]
let connectionString = @"Data Source=(LocalDb)\v11.0;Initial Catalog=AdventureWorks2012;Integrated Security=True"

[<Literal>]
let query = "
    SELECT TOP(@TopN) FirstName, LastName, SalesYTD 
    FROM Sales.vSalesPerson
    WHERE CountryRegionName = @regionName AND SalesYTD > @salesMoreThan 
    ORDER BY SalesYTD
" 

type SalesPersonQuery = SqlCommandProvider<query, connectionString>
let cmd = SalesPersonQuery()

cmd.AsyncExecute(TopN = 3L, regionName = "United States", salesMoreThan = 1000000M) 
|> Async.RunSynchronously

//output
seq
    [("Pamela", "Ansman-Wolfe", 1352577.1325M);
     ("David", "Campbell", 1573012.9383M);
     ("Tete", "Mensa-Annan", 1576562.1966M)]

(**

System requirements
-------------------------------------

 * SQL Server 2012 and up or SQL Azure Database at compile-time 
 * .NET 4.0 and higher

Features at glance
-------------------------------------

* Static type with 2 methods per `SqlCommandProvider<...>` declaration:
    * `AsyncExecute` - for scalability scenarios 
    * `Execute` - convenience when needed
* Configuration
    * Command text (sql script) can be either inline or path to *.sql file
    * Connection string is either inline or name from config file (app.config is default for config file)
    * Connection string can be overridden at run-time via constructor optional parameter
    * Constructor optionally accepts `SqlTransaction` and uses associated connection to execute command
* Input:
    * Statically typed
    * Unbound sql variables/input parameters mapped to mandatory arguments for `AsyncExecute/Execute`
    * Set `AllParametersOptional` to true to make all parameters optional (nullable)
* Output:
    * Inferred static type for output. Configurable choice of `seq<Tuples>`, `seq<Records>`, `DataTable` or `seq<Maps>`. Each column mapped to item/property/key
    * Nullable output columns translate to the F# Option type
* Extra configuration options:
    * `SingleRow` hint forces singleton output instead of sequence

* SqlCommandProvider is of "erased types" kind. It can be used only from F#. 

Limitations
-------------------------------------
In addition to system requirements listed above SqlCommandProvider constrained by same limitations as two system meta-stored procedures 
it uses in implementation: [sys.sp\_describe\_undeclared\_parameters](http://technet.microsoft.com/en-us/library/ff878260.aspx) 
and [sys.sp\_describe\_first\_result\_set](http://technet.microsoft.com/en-us/library/ff878602.aspx). Look online for more details.

*)
