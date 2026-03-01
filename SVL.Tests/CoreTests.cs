using System;
using System.Threading.Tasks;
using SVL.Core.App;
using SVL.Core.Stardew;

namespace SVL.Tests;

[TestClass]
public class CoreTests
{
    [TestMethod]
    public void Lifecycle_ShouldLoadServices()
    {
        var started = Lifecycle.StartAsync(LifecycleState.Loading);
        started.GetAwaiter().GetResult();

        Assert.IsNotNull(Lifecycle.GetContext(null));
    }

    [TestMethod]
    public async Task ConfigService_ShouldLoadAndSaveConfig()
    {
        ConfigService.Initialize();

        var testData = new TestData { Value = "test" };
        await ConfigService.SaveConfigAsync(App.Configuration.ConfigSource.Local, "test", testData);

        var loaded = await ConfigService.LoadConfigAsync<TestData>(App.Configuration.ConfigSource.Local, "test");
        Assert.AreEqual("test", loaded.Value);
    }

    [TestMethod]
    public void StardewCoreService_ShouldBeAttributeMarked()
    {
        var attr = typeof(StardewCoreService).GetCustomAttributes(typeof(LifecycleServiceAttribute), false);
        Assert.AreEqual(1, attr.Length);
        Assert.AreEqual(LifecycleState.Loading, ((LifecycleServiceAttribute)attr[0]).State);
    }

    private class TestData
    {
        public string Value { get; set; }
    }
}
