using System.Text.Json;
using StWorkbench.Toolchain;

internal static class ConfigurationDownloadWorkflowTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var kind in new[] { "axis", "hardware" })
        foreach (var wasRunning in new[] { true, false })
        {
            var h = new Host(kind, wasRunning);
            try
            {
                var outcome = await ProgramDownloadWorkflow.ExecuteAsync(new(new('b',64), "bytes", "checksum"), h);
                if (outcome.State != ProgramDownloadState.Blocked || !h.Events.SequenceEqual(new[] { "stage", "compile", "configuration-plan", "mismatch" })) throw new Exception("program download altered mismatching hardware or interrupted old program: " + string.Join(',',h.Events));
                Console.WriteLine("PASS scoped program download refuses " + kind + " mismatch without stopping or applying hardware");
            }
            catch (Exception e) { failures++; Console.WriteLine("FAIL scoped program configuration: " + e.Message); }
        }
        return failures;
    }
    private sealed class Host(string kind, bool running) : IProgramDownloadHost
    {
        public bool RequiresConfigurationCheck => true;
        public bool CanApplyAfterConfiguration => true;
        public bool CanApply => true;
        public bool IsCurrent => true;
        public List<string> Events { get; } = [];
        public Task<DeploymentResponse> SendAsync(string action, string? packageBase64=null,string? packageHash=null)
        {
            Events.Add(action);
            var plan = JsonSerializer.SerializeToElement(new { build_hash = new string('b',64), kind, identity = new string('c',64) });
            return Task.FromResult(new DeploymentResponse(true, action switch { "stage"=>"staged", "compile"=>"compiled", "configuration-plan"=>"configuration-planned", _=>running?"running":"stopped" }, "id", "", JsonSerializer.SerializeToElement(new{build_hash=new string('a',64)}), null, AxisReady:true, ConfigurationPlan:plan));
        }
        public void ReportConfigurationMismatch(ProgramConfigurationPlan plan) => Events.Add("mismatch");
        public Task<DeploymentResponse> ApplyConfigurationAsync(string identity) { Events.Add("configuration-apply"); throw new Exception("program download must never apply configuration"); }
        public void ApplyConfiguration(DeploymentResponse response) => throw new Exception("must not mutate configuration");
        public void ApplyCompilation(DeploymentResponse response) { }
        public Task<bool> ConfirmRestartAsync(ProgramRestartNotice notice) { Events.Add("confirm"); return Task.FromResult(true); }
        public Task BeforeProgramChangeAsync() { Events.Add("invalidate"); return Task.CompletedTask; }
    }
}
