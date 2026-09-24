return args is ["dev-bootstrap"]
    ? await SqlObserver.Cli.DevelopmentPostgreSqlBootstrapHost.RunAsync().ConfigureAwait(false)
    : await SqlObserver.Cli.LabPostgreSqlMigrationHost.RunAsync(args).ConfigureAwait(false);
