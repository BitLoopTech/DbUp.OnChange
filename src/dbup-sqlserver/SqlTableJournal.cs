using System;
using System.Data;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Helpers;
using DbUp.Support;

namespace DbUp.SqlServer
{
    /// <summary>
    /// An implementation of the <see cref="Engine.IJournal"/> interface which tracks version numbers for a 
    /// SQL Server database using a table called dbo.SchemaVersions.
    /// </summary>
    public class SqlTableJournal : TableJournal
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SqlTableJournal"/> class.
        /// </summary>
        /// <param name="connectionManager">The connection manager.</param>
        /// <param name="logger">The log.</param>
        /// <param name="schema">The schema that contains the table.</param>
        /// <param name="table">The table name.</param>
        /// <example>
        /// var journal = new TableJournal("Server=server;Database=database;Trusted_Connection=True", "dbo", "MyVersionTable");
        /// </example>
        public SqlTableJournal(Func<IConnectionManager> connectionManager, Func<IUpgradeLog> logger, Func<IHasher> hasher, string schema, string table)
            : base(connectionManager, logger, new SqlServerObjectParser(), hasher, schema, table)
        {
        }

        protected override string GetInsertJournalEntrySql(string @scriptName, string @applied, string @hash, SqlScript script)
        {
            if (script.RedeployOnChange)
            {
                return $"insert into {FqSchemaTableName} (ScriptName, Applied, Hash) values ({@scriptName}, {@applied}, {@hash})";
            }
            else
            {
                return $"insert into {FqSchemaTableName} (ScriptName, Applied) values ({@scriptName}, {@applied})";
            }
        }

        protected override string GetJournalEntriesSql()
        {
            throw new NotImplementedException();
        }

        protected override string GetJournalEntriesSql(Func<IDbCommand> dbCommandFactory)
        {
            if (RedeployableScriptSupportIsEnabled(dbCommandFactory))
            {
                // if redeployable script support is enabled, use Hash column value;
                // because redeployable script can appear multiple times with different Hash value, retrieve the most recent redeploy record
                return string.Format($@"select  [ScriptName], [Hash]
                                       from     {FqSchemaTableName} sv
                                                JOIN
                                                (select max(Applied) as MaxApplied from {FqSchemaTableName} group by [ScriptName]) as svSelf ON sv.Applied = svSelf.MaxApplied
                                       order    by [ScriptName]");
            }
            else
            {
                // if redeployable script support is NOT enabled, set Hash to null
                return string.Format($"select [ScriptName], NULL as [Hash] from {FqSchemaTableName} order by [ScriptName]");
            }
        }

        protected override string CreateSchemaTableSql(string quotedPrimaryKeyName)
        {
            return 
$@"create table {FqSchemaTableName} (
    [Id] int identity(1,1) not null constraint {quotedPrimaryKeyName} primary key,
    [ScriptName] nvarchar(255) not null,
    [Applied] datetime not null
)";
        }

        protected override string CreateHashColumnSql()
        {
            return string.Format($"alter table {FqSchemaTableName} add [Hash] varchar(100)");
        }
    }
}