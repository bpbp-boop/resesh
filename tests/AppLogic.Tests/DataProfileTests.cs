using Resesh.App;

namespace Resesh.AppLogic.Tests;

public sealed class DataProfileTests
{
    private const string Exe = @"C:\Program Files\Resesh\Resesh.App.exe";

    [Fact]
    public void No_options_is_the_default_profile()
    {
        var profile = DataProfile.Parse([Exe, "--open", "lab"]);
        Assert.True(profile.IsDefault);
        Assert.Null(profile.Error);
        Assert.Equal($"\"{Exe}\"", profile.RelaunchCommand(Exe));
    }

    [Fact]
    public void Relative_data_dir_is_made_absolute_for_the_relaunch_command()
    {
        var profile = DataProfile.Parse([Exe, "--data-dir", @"scratch\one"], @"D:\work");
        Assert.False(profile.IsDefault);
        Assert.Equal(@"D:\work\scratch\one", profile.DataDirectory);
        Assert.Equal($"\"{Exe}\" --data-dir \"D:\\work\\scratch\\one\"", profile.RelaunchCommand(Exe));
    }

    [Theory]
    [InlineData("--DEMO")]
    [InlineData("--Demo")]
    public void Demo_matches_case_insensitively_and_relaunches_as_demo(string option)
    {
        var profile = DataProfile.Parse([Exe, option, "--data-dir", @"C:\scratch"]);
        Assert.True(profile.IsDemo);
        Assert.False(profile.IsDefault);
        Assert.Null(profile.DataDirectory);
        Assert.Equal($"\"{Exe}\" --demo", profile.RelaunchCommand(Exe));
    }

    [Fact]
    public void Data_dir_option_matches_case_insensitively()
    {
        Assert.Equal(@"C:\scratch", DataProfile.Parse([Exe, "--Data-Dir", @"C:\scratch"]).DataDirectory);
    }

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--data-dir|")]
    [InlineData("--data-dir|--open|lab")]
    public void Data_dir_without_a_folder_is_an_error_not_the_real_profile(string options)
    {
        var profile = DataProfile.Parse([Exe, .. options.Split('|')]);
        Assert.NotNull(profile.Error);
        Assert.False(profile.IsDefault);
        Assert.Null(profile.DataDirectory);
    }

    [Fact]
    public void Option_after_a_missing_folder_is_still_read()
    {
        Assert.True(DataProfile.Parse([Exe, "--data-dir", "--demo"]).IsDemo);
    }

    [Fact]
    public void First_data_dir_wins()
    {
        Assert.Equal(@"C:\one", DataProfile.Parse([Exe, "--data-dir", @"C:\one", "--data-dir", @"C:\two"]).DataDirectory);
    }

    [Theory]
    [InlineData(@"C:\with space\")]
    [InlineData("C:\\quote\"d")]
    public void Quoted_arguments_split_back_to_the_original(string value)
    {
        // CommandLineToArgvW rules: 2n backslashes before a quote are n, and \" is a quote.
        var quoted = DataProfile.Quote(value);
        Assert.StartsWith("\"", quoted);
        Assert.EndsWith("\"", quoted);
        Assert.Equal(value, Unquote(quoted));
    }

    private static string Unquote(string quoted)
    {
        var result = new System.Text.StringBuilder();
        var body = quoted[1..^1];
        for (var i = 0; i < body.Length; i++)
        {
            var backslashes = 0;
            while (i < body.Length && body[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i < body.Length && body[i] == '"')
            {
                result.Append('\\', backslashes / 2);
                result.Append('"');
            }
            else
            {
                result.Append('\\', i == body.Length ? backslashes / 2 : backslashes);
                if (i < body.Length)
                    result.Append(body[i]);
            }
        }
        return result.ToString();
    }
}
