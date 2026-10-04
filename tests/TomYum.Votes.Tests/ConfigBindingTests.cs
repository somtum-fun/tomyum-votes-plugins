using System;
using System.Collections.Generic;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace TomYum.Votes.Tests;

/// <summary>
/// The setting names in the README, read the way TomYum reads them: environment variables with the
/// TOMYUM_ prefix ("__" for ":"), the plugin's section Plugins:&lt;id&gt;, and PluginConfig.Get
/// (section.GetSection(key).Get&lt;T&gt;()). A prefix of our own keeps the test off the real one.
/// </summary>
public sealed class ConfigBindingTests
{
    [Fact]
    public void Env_vars_with_the_dotted_plugin_id_reach_the_plugin_section()
    {
        const string Prefix = "TOMYUMVOTESTEST_";
        var variables = new Dictionary<string, string>
        {
            [Prefix + "PLUGINS__TRUSTED__0"] = "tomyum.metrics",
            [Prefix + "PLUGINS__TRUSTED__1"] = "tomyum.votes",
            [Prefix + "PLUGINS__tomyum.votes__apiKey"] = "secret-from-osl",
            [Prefix + "PLUGINS__TOMYUM.VOTES__DAYSPERVOTE"] = "2",
        };
        try
        {
            foreach (var (name, value) in variables)
                Environment.SetEnvironmentVariable(name, value);

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix: Prefix).Build();

            // PluginHostService: Plugins:<manifest id>, and PluginHostOptions.Trusted.
            var section = configuration.GetSection("Plugins:tomyum.votes");
            Assert.Equal("secret-from-osl", section.GetSection("apiKey").Get<string>());
            Assert.Equal(2, section.GetSection("daysPerVote").Get<int>());
            Assert.False(section.GetSection("voteUrl").Exists());
            Assert.Equal(
                new[] { "tomyum.metrics", "tomyum.votes" },
                configuration.GetSection("Plugins:Trusted").Get<List<string>>());
        }
        finally
        {
            foreach (var name in variables.Keys)
                Environment.SetEnvironmentVariable(name, null);
        }
    }
}
