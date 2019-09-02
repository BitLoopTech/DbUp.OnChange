using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Helpers;

// ReSharper disable MemberCanBePrivate.Global
namespace DbUp.Support
{
    /// <summary>
    /// The base class for Journal implementations that use a table.
    /// </summary>
    public abstract class TableJournal : IJournal
    {
        readonly ISqlObjectParser sqlObjectParser;
        bool journalExists;

        /// <summary>
        /// Initializes a new instance of the <see cref="TableJournal"/> class.
        /// </summary>
        /// <param name="connectionManager">The connection manager.</param>
        /// <param name="logger">The log.</param>
        /// <param name="sqlObjectParser"></param>
        /// <param name="hasher">Script content hasher</param>
        /// <param name="schema">The schema that contains the table.</param>
        /// <param name="table">The table name.</param>
        protected TableJournal(
            Func<IConnectionManager> connectionManager,
            Func<IUpgradeLog> logger,
            ISqlObjectParser sqlObjectParser,
            Func<IHasher> hasher,
            string schema, string table)
        {
            this.sqlObjectParser = sqlObjectParser;
            ConnectionManager = connectionManager;
            Log = logger;
            UnquotedSchemaTableName = table;
            SchemaTableSchema = schema;
            FqSchemaTableName = string.IsNullOrEmpty(schema)
                ? sqlObjectParser.QuoteIdentifier(table)
                : sqlObjectParser.QuoteIdentifier(schema) + "." + sqlObjectParser.QuoteIdentifier(table);

            Hasher = hasher;
            FirstRedeployExecution = true;
        }

        protected string SchemaTableSchema { get; private set; }

        /// <summary>
        /// Schema table name, no schema and unquoted
        /// </summary>
        protected string UnquotedSchemaTableName { get; private set; }

        /// <summary>
        /// Fully qualified schema table name, includes schema and is quoted.
        /// </summary>
        protected string FqSchemaTableName { get; private set; }

        protected Func<IConnectionManager> ConnectionManager { get; private set; }

        protected Func<IHasher> Hasher { get; private set; }

        protected bool FirstRedeployExecution { get; private set; }

        protected Func<IUpgradeLog> Log { get; private set; }

        public IEnumerable<ExecutedSqlScript> GetExecutedScripts()
        {
            return ConnectionManager().ExecuteCommandsWithManagedConnection(dbCommandFactory =>
            {
                if (journalExists || DoesTableExist(dbCommandFactory))
                {
                    Log().WriteInformation("Fetching list of already executed scripts.");

                    var scripts = new List<ExecutedSqlScript>();

                    using (var command = GetJournalEntriesCommand(dbCommandFactory))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                scripts.Add(new ExecutedSqlScript
                                {
                                    Name = reader["ScriptName"].ToString(),
                                    Hash = (reader["Hash"] == DBNull.Value) ? null : reader["Hash"].ToString()
                                });
                            }
                        }
                    }

                    return scripts;
                }
                else
                {
                    Log().WriteInformation("Journal table does not exist");
                    return Enumerable.Empty<ExecutedSqlScript>();
                }
            });
        }

        /// <summary>
        /// Records a database upgrade for a database specified in a given connection string.
        /// </summary>
        /// <param name="script">The script.</param>
        /// <param name="dbCommandFactory"></param>
        public virtual void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
        {
            EnsureTableExistsAndIsLatestVersion(dbCommandFactory);
           
            // add redeployable script support if activated
            if (FirstRedeployExecution && script.RedeployOnChange)
            {
                var exists = RedeployableScriptSupportIsEnabled();
                
                if (!exists)
                {
                    Log().WriteInformation($"Adding redeployable script support to the {FqSchemaTableName} table");

                    using (var command = GetCreateHashColumnCommand(dbCommandFactory))
                    {
                        command.ExecuteNonQuery();

                        Log().WriteInformation($"Redeployable script support has been added to {FqSchemaTableName} table");
                    }
                }

                FirstRedeployExecution = false;
            }

            using (var command = GetInsertScriptCommand(dbCommandFactory, script))
            {
                command.ExecuteNonQuery();
            }
        }

        protected IDbCommand GetInsertScriptCommand(Func<IDbCommand> dbCommandFactory, SqlScript script)
        {
            var command = dbCommandFactory();

            var scriptNameParam = command.CreateParameter();
            scriptNameParam.ParameterName = "scriptName";
            scriptNameParam.Value = script.Name;
            command.Parameters.Add(scriptNameParam);

            var appliedParam = command.CreateParameter();
            appliedParam.ParameterName = "applied";
            appliedParam.Value = DateTime.Now;
            command.Parameters.Add(appliedParam);

            if (script.RedeployOnChange)
            {
                var hashParam = command.CreateParameter();
                hashParam.ParameterName = "hash";

                if (script.RedeployOnChange)
                {
                    hashParam.Value = Hasher().GetHash(script.Contents);
                }
                else
                {
                    hashParam.Value = DBNull.Value;
                }

                command.Parameters.Add(hashParam);
            }

            command.CommandText = GetInsertJournalEntrySql("@scriptName", "@applied", "@hash", script);
            command.CommandType = CommandType.Text;
            return command;
        }

        protected IDbCommand GetJournalEntriesCommand(Func<IDbCommand> dbCommandFactory)
        {
            var command = dbCommandFactory();
            command.CommandText = GetJournalEntriesSql(dbCommandFactory) ?? GetJournalEntriesSql();
            command.CommandType = CommandType.Text;
            return command;
        }

        protected IDbCommand GetCreateTableCommand(Func<IDbCommand> dbCommandFactory)
        {
            var command = dbCommandFactory();
            var primaryKeyName = sqlObjectParser.QuoteIdentifier("PK_" + UnquotedSchemaTableName + "_Id");
            command.CommandText = CreateSchemaTableSql(primaryKeyName);
            command.CommandType = CommandType.Text;
            return command;
        }

        protected IDbCommand GetCreateHashColumnCommand(Func<IDbCommand> dbCommandFactory)
        {
            var command = dbCommandFactory();
            command.CommandText = CreateHashColumnSql();
            command.CommandType = CommandType.Text;

            return command;
        }

        /// <summary>
        /// Sql for inserting a journal entry
        /// </summary>
        /// <param name="scriptName">Name of the script name param (i.e @scriptName)</param>
        /// <param name="applied">Name of the applied param (i.e @applied)</param>
        /// <param name="hash">Name of the hash param (i.e @applied)</param>
        /// <param name="script">Script to insert</param>
        /// <returns></returns>
        protected abstract string GetInsertJournalEntrySql(string @scriptName, string @applied, string @hash, SqlScript script);

        /// <summary>
        /// Sql for getting the journal entries
        /// </summary>
        protected abstract string GetJournalEntriesSql();

        protected virtual string GetJournalEntriesSql(Func<IDbCommand> dbCommandFactory)
        {
            return null;
        }

        /// <summary>
        /// Sql for creating journal table
        /// </summary>
        /// <param name="quotedPrimaryKeyName">Following PK_{TableName}_Id naming</param>
        protected abstract string CreateSchemaTableSql(string quotedPrimaryKeyName);

        /// <summary>
        /// Sql for adding hash column to journal table
        /// </summary>
        protected abstract string CreateHashColumnSql();

        /// <summary>
        /// Unquotes a quoted identifier.
        /// </summary>
        /// <param name="quotedIdentifier">identifier to unquote.</param>
        protected string UnquoteSqlObjectName(string quotedIdentifier)
        {
            return sqlObjectParser.UnquoteIdentifier(quotedIdentifier);
        }

        protected virtual void OnTableCreated(Func<IDbCommand> dbCommandFactory)
        {
            // TODO: Now we could run any migration scripts on it using some mechanism to make sure the table is ready for use.
        }

        public virtual void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
        {
            if (!journalExists && !DoesTableExist(dbCommandFactory))
            {
                Log().WriteInformation(string.Format("Creating the {0} table", FqSchemaTableName));
                // We will never change the schema of the initial table create.
                using (var command = GetCreateTableCommand(dbCommandFactory))
                {
                    command.ExecuteNonQuery();
                }

                Log().WriteInformation(string.Format("The {0} table has been created", FqSchemaTableName));

                OnTableCreated(dbCommandFactory);
            }

            journalExists = true;
        }

        protected bool DoesTableExist(Func<IDbCommand> dbCommandFactory)
        {
            Log().WriteInformation("Checking whether journal table exists..");
            using (var command = dbCommandFactory())
            {
                command.CommandText = DoesTableExistSql();
                command.CommandType = CommandType.Text;
                var executeScalar = command.ExecuteScalar();
                if (executeScalar == null)
                    return false;
                if (executeScalar is long)
                    return (long) executeScalar == 1;
                if (executeScalar is decimal)
                    return (decimal)executeScalar == 1;
                return (int) executeScalar == 1;
            }
        }

        /// <summary>
        /// Returns whether script support is enabled before starting applying changes
        /// </summary>
        public bool ScriptSupportIsEnabled()
        {
            return ConnectionManager().ExecuteCommandsWithManagedConnection(dbCommandFactory => DoesTableExist(dbCommandFactory));
        }

        /// <summary>
        /// Returns whether redeployable script support is enabled before starting applying changes
        /// </summary>
        public bool RedeployableScriptSupportIsEnabled()
        {
            return ConnectionManager().ExecuteCommandsWithManagedConnection(dbCommandFactory =>
            {
                try
                {
                    using (var command = dbCommandFactory())
                    {
                        return VerifyColumnExistsCommand(command, UnquotedSchemaTableName, "Hash", SchemaTableSchema);
                    }
                }
                catch (DbException)
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Returns whether redeployable script support is enabled before starting applying changes
        /// </summary>
        public bool RedeployableScriptSupportIsEnabled(Func<IDbCommand> dbCommandFactory)
        {
            try
            {
                IDbCommand dbCommand = dbCommandFactory();
                return VerifyColumnExistsCommand(dbCommand, UnquotedSchemaTableName, "Hash", SchemaTableSchema);
            }
            catch (DbException)
            {
                return false;
            }
        }

        /// <summary>Verify, using database-specific queries, if the table exists in the database.</summary>
        /// <returns>1 if table exists, 0 otherwise</returns>
        protected virtual string DoesTableExistSql()
        {
            return string.IsNullOrEmpty(SchemaTableSchema)
                ? string.Format("select 1 from INFORMATION_SCHEMA.TABLES where TABLE_NAME = '{0}'", UnquotedSchemaTableName)
                : string.Format("select 1 from INFORMATION_SCHEMA.TABLES where TABLE_NAME = '{0}' and TABLE_SCHEMA = '{1}'", UnquotedSchemaTableName, SchemaTableSchema);
        }

        /// <summary>Verify, using database-specific queries, if the column exists in the database table.</summary>
        /// <param name="command">The <c>IDbCommand</c> to be used for the query</param>
        /// <param name="tableName">The name of the table</param>
        /// <param name="columnName">The name of the column</param>
        /// <param name="schemaName">The schema for the table</param>
        /// <returns>True if column exists, false otherwise</returns>
        protected virtual bool VerifyColumnExistsCommand(IDbCommand command, string tableName, string columnName, string schemaName)
        {
            command.CommandText = string.IsNullOrEmpty(schemaName)
                            ? string.Format("select 1 from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = '{0}' and COLUMN_NAME = '{1}'", tableName, columnName)
                            : string.Format("select 1 from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = '{0}' and TABLE_SCHEMA = '{1}' and COLUMN_NAME = '{2}'", tableName, schemaName, columnName);
            command.CommandType = CommandType.Text;
            var result = command.ExecuteScalar() as int?;
            return result == 1;
        }
    }
}
