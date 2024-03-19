using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using HotChocolate;
using HotChocolate.Execution;
using MovieTheater.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using MovieTheater.Services;

namespace MovieTheater.Gql
{
    [Command(name: "export gql", Description = "Save the graphql schema sdl to a file")]
    public class ExportGqlSchemaCommand : BasicDICommand, ICommand
    {
        [CommandParameter(0, Description = "Output file path")]
        public FileInfo OutputFile { get; set; }

        public ExportGqlSchemaCommand(MovieTheaterConfiguration config) : base(config) { }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var logger = GetRequiredService<ILogger<ExportGqlSchemaCommand>>();

            if (OutputFile.Exists)
            {
                logger.LogInformation("Output file {file} already exists and will be overwritten", OutputFile.FullName);
            }

            IServiceCollection serviceCollection = new ServiceCollection();
            serviceCollection.AddMovieTheaterGql();

            IServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
            var executorResolver = serviceProvider.GetRequiredService<IRequestExecutorResolver>();
            var requestExecutor = await executorResolver.GetRequestExecutorAsync(GqlServiceExtensions.SchemaName);

            using (StreamWriter writer = File.CreateText(OutputFile.FullName))
            {
                SchemaPrinter.Serialize(requestExecutor.Schema, writer);
            }

            logger.LogInformation("Gql schema output to {file}", OutputFile.FullName);
        }
    }
}
