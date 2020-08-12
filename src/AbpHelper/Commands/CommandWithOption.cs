﻿using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using EasyAbp.AbpHelper.Attributes;
using EasyAbp.AbpHelper.Extensions;
using Elsa.Activities;
using Elsa.Expressions;
using Elsa.Models;
using Elsa.Scripting.JavaScript;
using Elsa.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyAbp.AbpHelper.Commands
{
    public abstract class CommandWithOption<TOption> : CommandBase where TOption : CommandOptionsBase
    {
        public CommandWithOption(IServiceProvider serviceProvider, string name, string? description = null) : base(
            serviceProvider, name, description)
        {
            Logger = NullLogger<CommandWithOption<TOption>>.Instance;

            AddArgumentAndOptions();

            Handler = CommandHandler.Create((TOption option) => RunCommand(option));
        }

        protected virtual string OptionVariableName => CommandConsts.OptionVariableName;
        protected virtual string BaseDirectoryVariableName => CommandConsts.BaseDirectoryVariableName;
        protected virtual string ExcludeDirectoriesVariableName => CommandConsts.ExcludeDirectoriesVariableName;
        protected virtual string OverwriteVariableName => CommandConsts.OverwriteVariableName;

        public ILogger<CommandWithOption<TOption>> Logger { get; set; }

        protected virtual async Task RunCommand(TOption option)
        {
            option.Directory = GetBaseDirectory(option.Directory);

            await RunWorkflow(builder => CreateWorkflow(builder, option));
        }

        protected virtual WorkflowDefinitionVersion CreateWorkflow(IWorkflowBuilder builder, TOption option)
        {
            var activityBuilder = CreateBasicWorkflow(builder, option);

            return ConfigureBuild(activityBuilder, option).Build();
        }

        protected virtual IActivityBuilder CreateBasicWorkflow(IWorkflowBuilder builder, TOption option)
        {
            return builder.StartWith<SetVariable>(
                    step =>
                    {
                        step.VariableName = OptionVariableName;
                        step.ValueExpression = new JavaScriptExpression<TOption>($"({option.ToJson()})");
                    })
                .Then<SetVariable>(
                    step =>
                    {
                        step.VariableName = BaseDirectoryVariableName;
                        step.ValueExpression = new LiteralExpression(option.Directory);
                    })
                .Then<SetVariable>(
                    step =>
                    {
                        step.VariableName = ExcludeDirectoriesVariableName;
                        step.ValueExpression =
                            new JavaScriptExpression<string[]>(
                                $"{OptionVariableName}.{nameof(CommandOptionsBase.ExcludeDirectories)}");
                    })
                .Then<SetVariable>(
                    step =>
                    {
                        step.VariableName = OverwriteVariableName;
                        step.ValueExpression =
                            new JavaScriptExpression<bool>(
                                $"!{OptionVariableName}.{nameof(CommandOptionsBase.NoOverwrite)}");
                    });
        }

        protected virtual IActivityBuilder ConfigureBuild(IActivityBuilder activityBuilder, TOption option)
        {
            return activityBuilder;
        }

        protected virtual string GetBaseDirectory(string directory)
        {
            if (directory.IsNullOrEmpty())
            {
                directory = Environment.CurrentDirectory;
            }
            else if (!Directory.Exists(directory))
            {
                Logger.LogError($"Directory '{directory}' does not exist.");
                throw new DirectoryNotFoundException();
            }

            Logger.LogInformation($"Use directory: `{directory}`");

            return directory;
        }

        protected async Task RunWorkflow(Func<IWorkflowBuilder, WorkflowDefinitionVersion> builder)
        {
            var workflowBuilderFactory = ServiceProvider.GetRequiredService<Func<IWorkflowBuilder>>();
            var workflowBuilder = workflowBuilderFactory();

            var workflowDefinition = builder(workflowBuilder);
            // Start the workflow.
            Logger.LogInformation($"Command '{Name}' started.");
            var invoker = ServiceProvider.GetService<IWorkflowInvoker>();
            var ctx = await invoker.StartAsync(workflowDefinition);
            if (ctx.Workflow.Status == WorkflowStatus.Finished)
            {
                Logger.LogInformation($"Command '{Name}' finished successfully.");
            }
        }

        private void AddArgumentAndOptions()
        {
            foreach (var propertyInfo in typeof(TOption).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var optionAttribute = propertyInfo.GetCustomAttribute<OptionAttribute>();
                if (optionAttribute != null)
                {
                    var alias = new List<string>();
                    if (!optionAttribute.ShortName.IsNullOrWhiteSpace())
                    {
                        alias.Add("-" + optionAttribute.ShortName);
                    }

                    if (!optionAttribute.Name.IsNullOrWhiteSpace())
                    {
                        alias.Add("--" + optionAttribute.Name);
                    }

                    AddOption(new Option(alias.ToArray(), optionAttribute.Description)
                    {
                        Argument = new Argument
                        {
                            ArgumentType = propertyInfo.PropertyType,
                        },
                        Required = optionAttribute.Required
                    });

                    continue;
                }

                var argumentAttribute = propertyInfo.GetCustomAttribute<ArgumentAttribute>();
                if (argumentAttribute != null)
                {
                    AddArgument(new Argument(argumentAttribute.Name)
                    {
                        ArgumentType = propertyInfo.PropertyType,
                        Description = argumentAttribute.Description
                    });
                }
            }
        }
    }
}