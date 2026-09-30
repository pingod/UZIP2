using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 导出件要能安全地躺在 U 盘里: 没有口令就还原不出密码，改一个字节也会被识破。
    public class VaultTransferTests
    {
        const string Pass = "correct horse battery staple";

        static List<PasswordEntry> Sample() => new List<PasswordEntry>
        {
            new PasswordEntry { Name = "work", Text = "Alpha#2026", SuccessCount = 7 },
            new PasswordEntry { Name = "", Text = "beta-only-pw" },
            new PasswordEntry { Name = "paper", Text = "one-shot-9", IsPaper = true },
        };

        [Fact]
        public void Round_trip_keeps_names_counts_and_paper_flag()
        {
            var file = VaultTransfer.Export(Sample(), Pass);
            var back = VaultTransfer.Import(file, Pass);

            Assert.Equal(3, back.Count);
            Assert.Equal("work", back[0].Name);
            Assert.Equal("Alpha#2026", back[0].Text);
            Assert.Equal(7, back[0].SuccessCount);
            Assert.False(back[0].IsPaper);
            Assert.Equal("beta-only-pw", back[1].Text);
            Assert.True(back[2].IsPaper);
            Assert.Equal("one-shot-9", back[2].Text);
        }

        [Fact]
        public void Exported_file_never_contains_the_plaintext()
        {
            var file = VaultTransfer.Export(Sample(), Pass);
            foreach (var pw in new[] { "Alpha#2026", "beta-only-pw", "one-shot-9" })
                Assert.DoesNotContain(pw, file, StringComparison.Ordinal);
            // 口令本身也不该以任何形式落盘
            Assert.DoesNotContain(Pass, file, StringComparison.Ordinal);
            Assert.DoesNotContain("DPAPI:", file, StringComparison.Ordinal);
        }

        [Fact]
        public void Envelope_declares_format_and_kdf()
        {
            using var doc = JsonDocument.Parse(VaultTransfer.Export(Sample(), Pass));
            var root = doc.RootElement;
            Assert.Equal("UZIP-Vault", root.GetProperty("format").GetString());
            Assert.Equal(1, root.GetProperty("v").GetInt32());
            Assert.Equal("PBKDF2-SHA256", root.GetProperty("kdf").GetString());
            Assert.Equal(210000, root.GetProperty("iter").GetInt32());
        }

        [Fact]
        public void Two_exports_of_the_same_data_differ()
        {
            var a = VaultTransfer.Export(Sample(), Pass);
            var b = VaultTransfer.Export(Sample(), Pass);
            Assert.NotEqual(GetProp(a, "data"), GetProp(b, "data"));
        }

        [Fact]
        public void Wrong_passphrase_is_rejected_with_a_clear_message()
        {
            var file = VaultTransfer.Export(Sample(), Pass);
            var ex = Assert.Throws<VaultException>(() => VaultTransfer.Import(file, "wrong passphrase here"));
            Assert.Contains("口令错误", ex.Message);
        }

        [Fact]
        public void Tampered_payload_fails_authentication()
        {
            var file = VaultTransfer.Export(Sample(), Pass);
            var data = Convert.FromBase64String(GetProp(file, "data"));
            data[0] ^= 0xFF;
            var tampered = ReplaceProp(file, "data", Convert.ToBase64String(data));
            Assert.Throws<VaultException>(() => VaultTransfer.Import(tampered, Pass));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("short")]
        public void Weak_passphrase_is_refused_on_both_ends(string pass)
        {
            Assert.Throws<VaultException>(() => VaultTransfer.Export(Sample(), pass));
            Assert.Throws<VaultException>(() => VaultTransfer.Import("{}", pass));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{ not json")]
        [InlineData("{}")]
        [InlineData("{\"format\":\"Other\",\"v\":1,\"kdf\":\"PBKDF2-SHA256\",\"iter\":1000,\"salt\":\"AAAA\",\"iv\":\"AAAA\",\"tag\":\"AAAA\",\"data\":\"AAAA\"}")]
        public void Junk_input_throws_vault_exception_not_a_random_one(string json)
        {
            Assert.Throws<VaultException>(() => VaultTransfer.Import(json, Pass));
        }

        [Fact]
        public void Empty_book_round_trips_to_empty_list()
        {
            var file = VaultTransfer.Export(Array.Empty<PasswordEntry>(), Pass);
            Assert.Empty(VaultTransfer.Import(file, Pass));
        }

        [Fact]
        public void Entries_without_text_are_dropped()
        {
            var withBlank = Sample();
            withBlank.Add(new PasswordEntry { Name = "empty", Text = "" });
            Assert.Equal(3, VaultTransfer.Import(VaultTransfer.Export(withBlank, Pass), Pass).Count);
        }

        [Fact]
        public void Unicode_passwords_survive_the_trip()
        {
            var entries = new List<PasswordEntry> { new PasswordEntry { Name = "中文", Text = "密码🔐with-emoji" } };
            var back = VaultTransfer.Import(VaultTransfer.Export(entries, Pass), Pass);
            Assert.Equal("密码🔐with-emoji", back[0].Text);
            Assert.Equal("中文", back[0].Name);
        }

        static string GetProp(string json, string name)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty(name).GetString();
        }

        static string ReplaceProp(string json, string name, string value)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json).AsObject();
            node[name] = System.Text.Json.Nodes.JsonValue.Create(value);
            return node.ToJsonString();
        }
    }
}
