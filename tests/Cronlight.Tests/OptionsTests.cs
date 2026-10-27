using Microsoft.Extensions.Configuration;

namespace Cronlight.Tests;

public class OptionsTests
{
    [Fact]
    public void A_schedulers_settings_are_added_when_first_used_and_bound_from_configuration()
    {
        var options = new CronlightOptions();
        options.Schedulers["Billing"].JobAssemblies.Add(typeof(OptionsTests).Assembly);
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cronlight:Schedulers:Billing:ViewPolicy"] = "BillingTeam",
                ["Cronlight:Schedulers:Reports:ManagePolicy"] = "Admins",
            })
            .Build().GetSection("Cronlight").Bind(options);

        Assert.Equal(["Billing", "Reports"], options.Schedulers.Keys.Order());
        Assert.Equal("BillingTeam", options.Schedulers["Billing"].ViewPolicy);
        Assert.Single(options.Schedulers["Billing"].JobAssemblies);
        Assert.Equal("Admins", options.Schedulers["Reports"].ManagePolicy);
        Assert.False(options.Schedulers.ContainsKey("Other"));
    }
}
