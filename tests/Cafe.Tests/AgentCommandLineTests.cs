using Cafe.Agent.Core;

namespace Cafe.Tests;

public class AgentCommandLineTests
{
    [Fact]
    public void HashCode_with_value_is_parsed()
    {
        var result = AgentCommandLine.Parse(["--hash-code", "123456"]);
        Assert.Equal(new AgentCommand.HashCode("123456"), result);
    }

    [Fact]
    public void HashCode_without_value_is_an_error()
    {
        var result = Assert.IsType<AgentCommand.Error>(AgentCommandLine.Parse(["--hash-code"]));
        Assert.Contains("--hash-code", result.Message);
        Assert.Contains("Usage", AgentCommandLine.Usage);
    }

    [Fact]
    public void No_arguments_runs_and_help_is_recognised()
    {
        Assert.IsType<AgentCommand.Run>(AgentCommandLine.Parse([]));
        Assert.IsType<AgentCommand.Help>(AgentCommandLine.Parse(["--help"]));
    }

    [Fact]
    public void Unknown_switch_is_an_error_not_an_exception()
    {
        Assert.IsType<AgentCommand.Error>(AgentCommandLine.Parse(["--bogus"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_code_is_rejected(string code)
    {
        Assert.IsType<AgentCommand.Error>(AgentCommandLine.Parse(["--hash-code", code]));
    }

    [Fact]
    public void Overlong_code_is_rejected_and_limit_is_accepted()
    {
        Assert.IsType<AgentCommand.Error>(AgentCommandLine.Parse(["--hash-code", new string('x', 201)]));
        Assert.IsType<AgentCommand.HashCode>(AgentCommandLine.Parse(["--hash-code", new string('x', 200)]));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("12345")]
    public void Short_code_is_rejected(string code)
    {
        var error = Assert.IsType<AgentCommand.Error>(AgentCommandLine.Parse(["--hash-code", code]));
        Assert.Contains("at least", error.Message);
        Assert.IsType<AgentCommand.HashCode>(AgentCommandLine.Parse(["--hash-code", new string('x', AgentCommandLine.MinCodeLength)]));
    }
}
