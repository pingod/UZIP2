using UZIP2.Cli;
using Xunit;

namespace UZIP2.Tests
{
    public class CliParserTests
    {
        [Theory]
        [InlineData(new string[] { "extract", "a.zip" }, true)]
        [InlineData(new string[] { "list", "a.zip" }, true)]
        [InlineData(new string[] { "help" }, true)]
        [InlineData(new string[] { "--help" }, true)]
        [InlineData(new string[] { "--version" }, true)]
        [InlineData(new string[] { "checksum", "f" }, true)]
        // 右键菜单/命令行进来的开关绝不能被当成 CLI（要保留 GUI 单实例转发）
        [InlineData(new string[] { "--extract", "a.zip" }, false)]
        [InlineData(new string[] { "--extract-here", "a.zip" }, false)]
        [InlineData(new string[] { "--compress", "a.zip" }, false)]
        [InlineData(new string[] { "--register-shell" }, false)]
        [InlineData(new string[] { "--unregister-shell" }, false)]
        [InlineData(new string[] { "a.zip", "b.zip" }, false)]
        [InlineData(new string[] { }, false)]
        [InlineData(null, false)]
        public void IsCli_recognizes_command_words_not_shell_flags(string[]? args, bool expected)
            => Assert.Equal(expected, CliParser.IsCli(args!));

        [Theory]
        [InlineData("list", CliCommand.List)]
        [InlineData("test", CliCommand.Test)]
        [InlineData("extract", CliCommand.Extract)]
        [InlineData("compress", CliCommand.Compress)]
        [InlineData("checksum", CliCommand.Checksum)]
        [InlineData("vault", CliCommand.Vault)]
        [InlineData("config", CliCommand.Config)]
        [InlineData("log", CliCommand.Log)]
        [InlineData("shell", CliCommand.Shell)]
        [InlineData("watch", CliCommand.Watch)]
        [InlineData("update", CliCommand.Update)]
        [InlineData("help", CliCommand.Help)]
        [InlineData("version", CliCommand.Version)]
        public void Parse_maps_command_word(string cmd, CliCommand expected)
        {
            var r = CliParser.Parse(new[] { cmd });
            Assert.Equal(expected, r.Command);
            Assert.False(r.HasError);
        }

        [Fact]
        public void Parse_is_case_insensitive_on_command()
            => Assert.Equal(CliCommand.Extract, CliParser.Parse(new[] { "EXTRACT", "x" }).Command);

        [Fact]
        public void Parse_out_option_both_short_and_long()
        {
            Assert.Equal("d:", CliParser.Parse(new[] { "extract", "a.zip", "-o", "d:" }).Output);
            Assert.Equal("d:", CliParser.Parse(new[] { "extract", "a.zip", "--out", "d:" }).Output);
        }

        [Fact]
        public void Parse_positionals_kept_in_order()
        {
            var r = CliParser.Parse(new[] { "compress", "a.txt", "b.txt", "-o", "out" });
            Assert.Equal(new[] { "a.txt", "b.txt" }, r.Args);
            Assert.Equal("out", r.Output);
        }

        [Fact]
        public void Parse_entries_splits_on_semicolon_and_comma()
        {
            var r = CliParser.Parse(new[] { "extract", "a.zip", "--entries", "x/y;z,w" });
            Assert.Equal(new[] { "x/y", "z", "w" }, r.Entries);
        }

        [Fact]
        public void Parse_boolean_flags()
        {
            var r = CliParser.Parse(new[] { "extract", "a.zip", "--here", "--auto", "--json" });
            Assert.True(r.Here);
            Assert.True(r.Auto);
            Assert.True(r.Json);
        }

        [Fact]
        public void Parse_repeatable_exclude()
        {
            var r = CliParser.Parse(new[] { "compress", "d", "--exclude", "*.tmp", "--exclude", "node_modules" });
            Assert.Equal(new[] { "*.tmp", "node_modules" }, r.Exclude);
        }

        [Fact]
        public void Parse_level_requires_integer()
        {
            Assert.True(CliParser.Parse(new[] { "compress", "a", "--level", "abc" }).HasError);
            Assert.Equal(9, CliParser.Parse(new[] { "compress", "a", "--level", "9" }).Level);
        }

        [Fact]
        public void Parse_value_option_at_end_without_value_is_error()
        {
            var r = CliParser.Parse(new[] { "extract", "a.zip", "--password" });
            Assert.True(r.HasError);
        }

        [Fact]
        public void Parse_dashdash_treats_rest_as_positional()
        {
            var r = CliParser.Parse(new[] { "extract", "--", "--weird-name.zip" });
            Assert.Equal(new[] { "--weird-name.zip" }, r.Args);
        }

        [Fact]
        public void Parse_vault_subcommand_is_positional()
        {
            var r = CliParser.Parse(new[] { "vault", "add", "名字", "pw123" });
            Assert.Equal(CliCommand.Vault, r.Command);
            Assert.Equal(new[] { "add", "名字", "pw123" }, r.Args);
        }

        [Fact]
        public void Parse_quotes_are_stripped_from_tokens()
        {
            var r = CliParser.Parse(new[] { "extract", "\"my file.zip\"" });
            Assert.Equal(new[] { "my file.zip" }, r.Args);
        }

        [Fact]
        public void Parse_leading_double_dash_version_returns_version()
            => Assert.Equal(CliCommand.Version, CliParser.Parse(new[] { "--version" }).Command);

        [Fact]
        public void Parse_unknown_leading_flag_falls_back_to_help()
            => Assert.Equal(CliCommand.Help, CliParser.Parse(new[] { "--bogus", "x" }).Command);
    }
}
