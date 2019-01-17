This fork adds several additional script options to DbUp. Implemented only for SQL server.

The most important addition is the redeploy functionality for deploying stored procedures, views, functions, etc. 

DbUp provides an option to apply idempotent scripts using _NullJournal_ (http://dbup.readthedocs.io/en/latest/more-info/journaling)

```csharp
DeployChanges.To
  .SqlDatabase(connectionString)
  .WithScriptsEmbeddedInAssembly(
      Assembly.GetExecutingAssembly(),
      s => s.Contains("everytime"))
  .JournalTo(new NullJournal())
  .Build();
```

But the _NullJournal_ approach means that all scripts will be redeployed on each migration, which is not always a good thing because recreating a stored procedure or a view leads to a new execution plan in SQL Server and usage statistics would also be lost. 

This can be improved using _ScriptOptions_

```csharp
DeployChanges.To
    .SqlDatabase(connectionString)
    .WithScriptsFromFileSystem
    (
        Path.Combine(folderPath, "Migrations"), 
        new FileSystemScriptOptions() { IncludeSubDirectories = true },
        new ScriptOptions()
        {
            FirstDeploymentAsStartingPoint = true,
            IncludeSubDirectoryInName = true
        }
    )
    .WithScriptsFromFileSystem
    (
        Path.Combine(folderPath, "Programmability"),
        new FileSystemScriptOptions() { IncludeSubDirectories = true },
        new ScriptOptions()
        {
            RedeployOnChange = true,
            FirstDeploymentAsStartingPoint = true,
            DependencyOrderFilePath = Path.Combine(folderPath, "dependencies.txt"),
            IncludeSubDirectoryInName = true
        }
    )
    .WithTransaction() // apply all changes in a single transaction
    .Build();
```

With this setup, scripts in the _Migrations_ folder will be deployed only once and scripts in the _Programmability_ folder which are activated for redeployment, will be deployed again when their contents change. 

A possible structure of the _Migrations_ and _Programmability_ folders:

![Folder structure](https://raw.githubusercontent.com/szilarddavid/dbup.onchange/master/docs/images/onchange.png)

# NuGet package

[![NuGet version](https://badge.fury.io/nu/dbup.onchange-sqlserver.svg)](https://badge.fury.io/nu/dbup.onchange-sqlserver)

# Available ScriptOptions

### RedeployOnChange

Controls whether scripts should be redeployed on content change

### FirstDeploymentAsStartingPoint 

Controls whether the first deployment should be used as a starting point which means that scripts won't be deployed into the database during the first deployment, they will just be marked as processed. 

Useful when activating DbUp on an existing database and redeploying all scripts is not ideal

### IncludeSubDirectoryInName

Controls whether to include the subdirectory path of a script file in the name of the script.

By default DbUp marks scripts as executed by file name. But if you have scripts with the same name in different database schemas (stored in different folders) this will lead to issues. By including the subdirectory path in the name of the script it is ensured that script names will be unique.

Another solution would be to include the schema name in the script file, in which case this script option is not necessary.

### DependencyOrderFilePath 

Path to a file containing script dependencies

For example by providing a _dependencies.txt_ file:

```
firstView.sql
anotherView.sql
```

This will ensure that  _firstView.sql_ will be executed before _anotherView.sql_. Without a dependency order file, the scripts would be executed in alphabetical order.

Useful when having nested SQL views and the parent view references a child view which is not available when trying to create the parent view. With this approach it can be ensured that the child view is created before the parent.

Note: When used together with _IncludeSubDirectoryInName_ option, the scripts in the dependency file must include the path of the subdirectory in which the script is located, something like _Views/firstView.sql_ or _dbo/Views/firstView.sql_ (if you store the scripts in different folders per database schema)