using FactorioMCP.Services;
using Xunit;

namespace FactorioMCP.Tests.Services;

public class InventoryCompatibilityTests
{
    [Fact]
    public async Task InventoryQueries_ResolveLegacyAliasesCentrally()
    {
        var rcon = new ScriptedRconClient(["""{"entity":"stone-furnace","count":1}"""]);
        var service = new FactorioService(rcon);
        await service.InsertItemsAsync(-19, -5, "iron-ore", 2, "furnace_source");
        await service.RemoveItemsAsync(-19, -5, "iron-plate", 1, "furnace_result");
        await service.TransferAllItemsAsync(-19, -5, "assembling_machine_output");
        await service.GetEntityInventoryAsync(-19, -5, "assembling_machine_input");
        await service.InspectEntityAsync(-19, -5);
        await service.FindMissingInputsAsync(-19, -5);
        await service.CountItemInWorldAsync("iron-plate");
        await service.WaitForEntityInventoryAsync(-19, -5, "iron-plate", 1, "furnace_result",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));

        Assert.All(rcon.AllCommands, command =>
        {
            Assert.Contains("local function inventory_type", command);
            Assert.Contains("crafter_input", command);
            Assert.Contains("crafter_output", command);
            Assert.DoesNotMatch(@"defines\.inventory\.(furnace_source|furnace_result|assembling_machine_input|assembling_machine_output)\b", command);
        });
    }
}
