using Xunit;

namespace KinematicTrees.KtNode.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void NextStepValuesMatchAbiContract()
    {
        Assert.Equal(0u, (uint)NextStep.Continue);
        Assert.Equal(1u, (uint)NextStep.Stop);
        Assert.Equal(2u, (uint)NextStep.Recoverable);
        Assert.Equal(3u, (uint)NextStep.Fatal);
    }

    [Fact]
    public void RuntimeLoadsAbiVersionFromComposedEnvironment()
    {
        var version = Runtime.AbiVersion;
        Assert.Equal(1u, version.Major);
        Assert.Equal(2u, version.Minor);
        Assert.False(string.IsNullOrWhiteSpace(Runtime.BuildId));
        var runtimeVersion = Runtime.RuntimeVersion;
        Assert.Equal(0u, runtimeVersion.Major);
        Assert.Equal(1u, runtimeVersion.Minor);
        Assert.Equal(0u, runtimeVersion.Patch);
    }

    [Fact]
    public void DefaultNodeMethodsAreSafeAndHighLevel()
    {
        INode node = new EmptyNode();
        Assert.Equal(NextStep.Continue, node.Setup(null!));
        Assert.Equal(NextStep.Stop, node.Step(null!));
        Assert.Equal(NextStep.Stop, node.Close(null!));
    }

    private sealed class EmptyNode : INode { }
}
