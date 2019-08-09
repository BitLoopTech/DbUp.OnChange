using System;
using System.Collections.Generic;
using System.Linq;
using DbUp.Builder;
using DbUp.Engine.Filters;
using DbUp.Helpers;

namespace DbUp.Engine
{
    /// <summary>
    /// This class orchestrates the database upgrade process.
    /// </summary>
    public class UpgradeEngine
    {
        private readonly UpgradeConfiguration configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="UpgradeEngine"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        public UpgradeEngine(UpgradeConfiguration configuration)
        {
            this.configuration = configuration;
        }

        /// <summary>
        /// Determines whether the database is out of date and can be upgraded.
        /// </summary>
        public bool IsUpgradeRequired()
        {
            return GetScriptsToExecute().Count() != 0;
        }

        /// <summary>
        /// Tries to connect to the database.
        /// </summary>
        /// <param name="errorMessage">Any error message encountered.</param>
        /// <returns></returns>
        public bool TryConnect(out string errorMessage)
        {
            return configuration.ConnectionManager.TryConnect(configuration.Log, out errorMessage);
        }

        /// <summary>
        /// Performs the database upgrade.
        /// </summary>
        public DatabaseUpgradeResult PerformUpgrade()
        {
            var executed = new List<SqlScript>();

            string executedScriptName = null;
            try
            {
                using (configuration.ConnectionManager.OperationStarting(configuration.Log, executed))
                {

                    configuration.Log.WriteInformation("Beginning database upgrade");

                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    if (scriptsToExecute.Count == 0)
                    {
                        configuration.Log.WriteInformation("No new scripts need to be executed - completing.");
                        return new DatabaseUpgradeResult(executed, true, null);
                    }

                    configuration.ScriptExecutor.VerifySchema();

                    // check whether script support had already been enabled by a previous deployment
                    var scriptSupportIsEnabled = configuration.Journal.ScriptSupportIsEnabled();

                    // check whether redeployable script support had already been enabled by a previous deployment
                    var redeployableScriptSupportIsEnabled = configuration.Journal.RedeployableScriptSupportIsEnabled();

                    foreach (var script in scriptsToExecute)
                    {
                        executedScriptName = script.Name;

                        // if the first deployment should be treated as a starting point and it is in fact the first deployment, 
                        // treat the current deployment only as a baseline (do not execute scripts, only log them as executed)
                        if
                        (
                            script.FirstDeploymentAsStartingPoint                                    // deployment is marked as a baseline
                            &&
                            (
                                (script.RedeployOnChange && !redeployableScriptSupportIsEnabled)     // redeploy on change and it is the first deployment
                                ||
                                (!script.RedeployOnChange && !scriptSupportIsEnabled)                // not redeploy and it is the first deployment
                            )
                        )
                        {
                            // do nothing here
                        }
                        else
                        {
                            configuration.ScriptExecutor.Execute(script, configuration.Variables);
                        }

                        executed.Add(script);
                    }

                    configuration.Log.WriteInformation("Upgrade successful");
                    return new DatabaseUpgradeResult(executed, true, null);
                }
            }
            catch (Exception ex)
            {
                ex.Data.Add("Error occurred in script: ", executedScriptName);
                configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                return new DatabaseUpgradeResult(executed, false, ex);
            }
        }

        /// <summary>
        /// Returns a list of scripts that will be executed when the upgrade is performed
        /// </summary>
        /// <returns>The scripts to be executed</returns>
        public List<SqlScript> GetScriptsToExecute()
        {
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, new List<SqlScript>()))
            {
                return GetScriptsToExecuteInsideOperation();
            }
        }

        private List<SqlScript> GetScriptsToExecuteInsideOperation()
        {
            var allScripts = configuration.ScriptProviders.SelectMany(scriptProvider => {

                var providerScripts = scriptProvider.GetScripts(configuration.ConnectionManager).ToList();
                var dependencyOrderFilePath = scriptProvider.ScriptOptions.DependencyOrderFilePath;

                // if a dependency file is provided, order scripts based on that
                if (providerScripts.Count() > 0 && !string.IsNullOrEmpty(dependencyOrderFilePath))
                {
                    providerScripts = configuration.ScriptDependencyOrderer.GetScriptsOrderedByDependencies(providerScripts, dependencyOrderFilePath);
                }
                else
                {
                    // order scripts based on name
                    providerScripts = providerScripts.OrderBy(s => s.Name, configuration.ScriptNameComparer).ToList();
                }

                // if redeploy on change is activated, mark all script for redeploy
                if (scriptProvider.ScriptOptions.RedeployOnChange || scriptProvider.ScriptOptions.FirstDeploymentAsStartingPoint)
                {
                    providerScripts.ForEach(e => {
                        e.RedeployOnChange = scriptProvider.ScriptOptions.RedeployOnChange;
                        e.FirstDeploymentAsStartingPoint = scriptProvider.ScriptOptions.FirstDeploymentAsStartingPoint;
                    });
                }

                return providerScripts;
            });

            var executedScripts = configuration.Journal.GetExecutedScripts();
            IOrderedEnumerable<SqlScript> sorted;

            // if there is at least a provider with a custom dependency order, do not sort scripts as they are already sorted by dependencies
            if (configuration.ScriptProviders.Any(e => !string.IsNullOrEmpty(e.ScriptOptions.DependencyOrderFilePath)))
            {
                sorted = allScripts.OrderBy(s => 1);
            }
            else
            {
                sorted = allScripts.OrderBy(s => s.Name, configuration.ScriptNameComparer);
            }

            var filtered = configuration.ScriptFilter.Filter(sorted, executedScripts, configuration.ScriptNameComparer, configuration.Hasher);

            return filtered.ToList();
        }

        public List<string> GetExecutedScripts()
        {
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, new List<SqlScript>()))
            {
                return configuration.Journal.GetExecutedScripts().Select(e => e.Name)
                    .ToList();
            }
        }

        ///<summary>
        /// Creates version record for any new migration scripts without executing them.
        /// Useful for bringing development environments into sync with automated environments
        ///</summary>
        ///<returns></returns>
        public DatabaseUpgradeResult MarkAsExecuted()
        {
            var marked = new List<SqlScript>();
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, marked))
            {
                try
                {
                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    foreach (var script in scriptsToExecute)
                    {
                        configuration.ConnectionManager.ExecuteCommandsWithManagedConnection(
                            connectionFactory => configuration.Journal.StoreExecutedScript(script, connectionFactory));
                        configuration.Log.WriteInformation("Marking script {0} as executed", script.Name);
                        marked.Add(script);
                    }

                    configuration.Log.WriteInformation("Script marking successful");
                    return new DatabaseUpgradeResult(marked, true, null);
                }
                catch (Exception ex)
                {
                    configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                    return new DatabaseUpgradeResult(marked, false, ex);
                }
            }
        }

        public DatabaseUpgradeResult MarkAsExecuted(string latestScript)
        {
            var marked = new List<SqlScript>();
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, marked))
            {
                try
                {
                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    foreach (var script in scriptsToExecute)
                    {
                        configuration.ConnectionManager.ExecuteCommandsWithManagedConnection(
                            commandFactory => configuration.Journal.StoreExecutedScript(script, commandFactory));
                        configuration.Log.WriteInformation("Marking script {0} as executed", script.Name);
                        marked.Add(script);
                        if (script.Name.Equals(latestScript))
                        {
                            break;
                        }
                    }

                    configuration.Log.WriteInformation("Script marking successful");
                    return new DatabaseUpgradeResult(marked, true, null);
                }
                catch (Exception ex)
                {
                    configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                    return new DatabaseUpgradeResult(marked, false, ex);
                }
            }
        }
    }
}