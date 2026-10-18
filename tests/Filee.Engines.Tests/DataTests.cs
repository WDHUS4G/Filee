// JSON, JSON Lines, YAML, TOML, XML and CSV through the built-in data engine: types and structure carried over, the
// documented lossy cases (TOML root, XML attributes and repeats, YAML anchors, CSV tables only) and the routes through
// CSV to spreadsheets. Inputs are written in the test.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Tomlyn;
using Tomlyn.Model;

namespace Filee.Engines.Tests;

public class DataTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static DataTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private string Write(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private async Task<string> ConvertTextAsync(string name, string text, string target)
    {
        var output = await ConvertAsync(Write(name, text), target);
        var bytes = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
        if (target != "csv")
            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "UTF-8 without BOM");
        return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
    }

    private async Task<string> FailAsync(string name, string text, string target)
    {
        var job = await fx.ConvertAsync([Write(name, text)], new Preset { TargetFormat = target });
        Assert.Equal(JobState.Failed, job.State);
        return job.Files.Single().ErrorDetail ?? "";
    }

    private const string Sample = """
        {
          "name": "Filee 변환기",
          "version": 3,
          "ratio": 3.0,
          "big": 12345678901234567890,
          "enabled": true,
          "missing": null,
          "notes": "first line\nsecond line",
          "looksLikeABoolean": "true",
          "looksLikeANumber": "007",
          "empty": "",
          "tags": ["a", "b"],
          "nothing": [],
          "owner": { "id": 1, "email": "me@example.com" },
          "rows": [ { "x": 1 }, { "x": 2 } ]
        }
        """;

    [Fact]
    public async Task Json_to_yaml_keeps_types_quotes_ambiguous_text_and_indents_by_two()
    {
        var yaml = await ConvertTextAsync("sample.json", Sample, "yaml");

        Assert.Equal("""
            name: Filee 변환기
            version: 3
            ratio: 3.0
            big: 12345678901234567890
            enabled: true
            missing: null
            notes: |-
              first line
              second line
            looksLikeABoolean: "true"
            looksLikeANumber: "007"
            empty: ""
            tags:
              - a
              - b
            nothing: []
            owner:
              id: 1
              email: me@example.com
            rows:
              - x: 1
              - x: 2

            """.ReplaceLineEndings("\n"), yaml);
    }

    [Fact]
    public async Task Json_round_trips_through_yaml_toml_and_xml_where_the_format_allows()
    {
        var yaml = await ConvertAsync(Write("sample.json", Sample), "yaml");
        var back = await File.ReadAllTextAsync(await ConvertAsync(yaml, "json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"name\": \"Filee 변환기\"", back);
        Assert.Contains("\"ratio\": 3.0,", back);
        Assert.Contains("\"big\": 12345678901234567890,", back);
        Assert.Contains("\"missing\": null,", back);
        Assert.Contains("\"looksLikeABoolean\": \"true\",", back);
        Assert.Contains("\"notes\": \"first line\\nsecond line\",", back);
        Assert.Contains("  \"tags\": [\n    \"a\",", back); // two-space indentation

        var toml = await ConvertTextAsync("sample.json", Sample, "toml");
        Assert.DoesNotContain("missing", toml); // TOML has no null
        Assert.Contains("big = \"12345678901234567890\"\n", toml); // beyond 64-bit: kept as text
        Assert.Contains("ratio = 3.0\n", toml);
        Assert.Contains("\n[owner]\nid = 1\n", toml);
        Assert.Contains("\n[[rows]]\nx = 1\n\n[[rows]]\nx = 2\n", toml);
        var table = TomlSerializer.Deserialize<TomlTable>(toml)!; // valid TOML
        Assert.Equal("Filee 변환기", table["name"]);
        Assert.Equal(3L, table["version"]);

        var xml = await ConvertTextAsync("sample.json", Sample, "xml");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n  <name>Filee 변환기</name>\n", xml);
        Assert.Contains("  <missing />\n", xml);
        Assert.Contains("  <empty></empty>\n", xml);
        Assert.Contains("  <tags>a</tags>\n  <tags>b</tags>\n", xml);
    }

    [Fact]
    public async Task Yaml_anchors_merge_keys_and_core_schema_types_become_json()
    {
        const string Yaml = """
            # comments are dropped
            defaults: &defaults
              adapter: postgres
              port: 5432
            development:
              <<: *defaults
              port: 5433
              database: dev
            flags: [yes, no, on, ~, 0x1F, 0o17, .inf, -1.5e3, '42']
            when: 2026-10-16
            text: |
              line one
              line two
            """;

        var json = await ConvertTextAsync("config.yml", Yaml, "json");

        Assert.Contains("""
              "development": {
                "adapter": "postgres",
                "port": 5433,
                "database": "dev"
              },
            """.ReplaceLineEndings("\n"), json);
        Assert.Contains("""
              "flags": [
                "yes",
                "no",
                "on",
                null,
                31,
                15,
                "Infinity",
                -1500.0,
                "42"
              ],
            """.ReplaceLineEndings("\n"), json);
        Assert.Contains("\"when\": \"2026-10-16\"", json);
        Assert.Contains("\"text\": \"line one\\nline two\"", json); // the file ends without a line break
        Assert.DoesNotContain("comments", json);
    }

    [Fact]
    public async Task Several_yaml_documents_become_an_array_and_alias_bombs_are_refused()
    {
        var json = await ConvertTextAsync("docs.yaml", "a: 1\n---\nb: 2\n", "json");
        Assert.Equal("[\n  {\n    \"a\": 1\n  },\n  {\n    \"b\": 2\n  }\n]\n", json);

        var error = await FailAsync("bad.yaml", "key: [unclosed\n", "json");
        Assert.Contains("Not valid YAML", error);

        // "Billion laughs": each level repeats the one before ten times (10^8 values).
        var bomb = new StringBuilder("l0: &l0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 1; i <= 8; i++)
            bomb.Append($"l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 10))}]\n");
        error = await FailAsync("bomb.yaml", bomb.ToString(), "json");
        Assert.Contains("too many values", error);
    }

    [Fact]
    public async Task Toml_dates_tables_and_arrays_of_tables_become_json_and_yaml()
    {
        const string Toml = """
            title = "TOML 예제"
            released = 1979-05-27T07:32:00-08:00
            day = 2026-10-16

            [server]
            ports = [8000, 8001]
            limits = { cpu = 2.5, memory = "1G" }

            [[products]]
            name = "Hammer"
            sku = 738594937

            [[products]]
            name = "Nail"
            """;

        var json = await ConvertTextAsync("config.toml", Toml, "json");
        Assert.Contains("\"title\": \"TOML 예제\"", json);
        Assert.Contains("\"released\": \"1979-05-27T07:32:00-08:00\"", json);
        Assert.Contains("\"day\": \"2026-10-16\"", json);
        Assert.Contains("\"limits\": {\n      \"cpu\": 2.5,\n      \"memory\": \"1G\"\n    }", json);
        Assert.Contains("\"products\": [\n    {\n      \"name\": \"Hammer\",\n      \"sku\": 738594937\n    },", json);

        var yaml = await ConvertTextAsync("config.toml", Toml, "yaml");
        Assert.Contains("released: 1979-05-27T07:32:00-08:00\n", yaml);
        Assert.Contains("products:\n  - name: Hammer\n    sku: 738594937\n  - name: Nail\n", yaml);
    }

    [Fact]
    public async Task A_root_that_is_not_a_table_goes_under_items_in_toml()
    {
        var toml = await ConvertTextAsync("list.json", """[{"a": 1, "b": null}, {"a": 2}]""", "toml");
        Assert.Equal("[[items]]\na = 1\n\n[[items]]\na = 2\n", toml);

        toml = await ConvertTextAsync("value.json", "\"just text\"", "toml");
        Assert.Equal("items = \"just text\"\n", toml);
    }

    [Fact]
    public async Task Xml_attributes_repeats_text_and_namespaces_become_json()
    {
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- dropped -->
            <catalog xmlns:dc="http://purl.org/dc/elements/1.1/" version="2">
              <book id="b1" lang="ko">
                <dc:title>한국어 책</dc:title>
                <price currency="KRW">15000</price>
                <note/>
                <summary></summary>
                <body><![CDATA[a < b]]></body>
              </book>
              <book id="b2">
                <dc:title>Second</dc:title>
              </book>
            </catalog>
            """;

        var json = await ConvertTextAsync("catalog.xml", Xml, "json");

        Assert.Equal("""
            {
              "catalog": {
                "@xmlns:dc": "http://purl.org/dc/elements/1.1/",
                "@version": "2",
                "book": [
                  {
                    "@id": "b1",
                    "@lang": "ko",
                    "dc:title": "한국어 책",
                    "price": {
                      "@currency": "KRW",
                      "#text": "15000"
                    },
                    "note": null,
                    "summary": "",
                    "body": "a < b"
                  },
                  {
                    "@id": "b2",
                    "dc:title": "Second"
                  }
                ]
              }
            }

            """.ReplaceLineEndings("\n"), json);

        // And back: the same elements, attributes and namespace.
        var xml = await File.ReadAllTextAsync(await ConvertAsync(Write("catalog.json", json), "xml"), TestContext.Current.CancellationToken);
        Assert.Contains("<catalog xmlns:dc=\"http://purl.org/dc/elements/1.1/\" version=\"2\">", xml);
        Assert.Contains("    <dc:title>한국어 책</dc:title>\n    <price currency=\"KRW\">15000</price>\n    <note />\n    <summary></summary>\n    <body>a &lt; b</body>\n", xml);
        Assert.Equal(2, xml.Split("<book ").Length - 1);
    }

    [Fact]
    public async Task A_default_namespace_survives_xml_to_yaml_and_back()
    {
        const string Xml = "<feed xmlns=\"http://www.w3.org/2005/Atom\" xml:lang=\"ko\"><title>소식</title><entry><id>1</id></entry></feed>";

        var yaml = await ConvertTextAsync("feed.xml", Xml, "yaml");
        Assert.Equal("feed:\n  '@xmlns': http://www.w3.org/2005/Atom\n  '@xml:lang': ko\n  title: 소식\n  entry:\n    id: \"1\"\n", yaml);

        var xml = await ConvertTextAsync("feed.yaml", yaml, "xml");
        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<feed xmlns=\"http://www.w3.org/2005/Atom\" xml:lang=\"ko\">\n  <title>소식</title>\n  <entry>\n    <id>1</id>\n  </entry>\n</feed>\n", xml);
    }

    [Fact]
    public async Task Xml_in_euc_kr_and_names_that_are_not_xml_names()
    {
        var path = Write("old.xml", "<?xml version=\"1.0\" encoding=\"EUC-KR\"?><memo><to>홍길동</to></memo>", Encoding.GetEncoding("EUC-KR"));
        var json = await File.ReadAllTextAsync(await ConvertAsync(path, "json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"to\": \"홍길동\"", json);

        var xml = await ConvertTextAsync("names.json", """{"first name": "Ada", "1st": 1, "list": [[1, 2]]}""", "xml");
        Assert.Contains("<first_x0020_name>Ada</first_x0020_name>", xml);
        Assert.Contains("<_x0031_st>1</_x0031_st>", xml);
        Assert.Contains("<list>\n    <item>1</item>\n    <item>2</item>\n  </list>", xml);
        var back = await File.ReadAllTextAsync(await ConvertAsync(Write("names.xml", xml), "json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"first name\": \"Ada\"", back); // names unescaped again
    }

    [Fact]
    public async Task Csv_becomes_typed_records_and_back()
    {
        const string Csv = "name,zip,age,member,score\r\n홍길동,01234,42,TRUE,3.5\r\n\"Kim, Ada\",,7,false,\r\n";

        var json = await ConvertTextAsync("people.csv", Csv, "json");
        Assert.Equal("""
            [
              {
                "name": "홍길동",
                "zip": "01234",
                "age": 42,
                "member": true,
                "score": 3.5
              },
              {
                "name": "Kim, Ada",
                "zip": "",
                "age": 7,
                "member": false,
                "score": ""
              }
            ]

            """.ReplaceLineEndings("\n"), json);

        var csv = await ConvertTextAsync("people.json", json, "csv");
        Assert.Equal("name,zip,age,member,score\r\n홍길동,01234,42,true,3.5\r\n\"Kim, Ada\",,7,false,\r\n", csv);
    }

    [Fact]
    public async Task Tables_become_csv_in_every_shape_and_anything_else_is_refused()
    {
        // Wrapped array of objects, with one level of nesting and keys that only some rows have.
        var csv = await ConvertTextAsync("users.json",
            """{"users": [{"id": 1, "address": {"city": "Seoul"}}, {"id": 2, "vip": true}]}""", "csv");
        Assert.Equal("id,address.city,vip\r\n1,Seoul,\r\n2,,true\r\n", csv);

        // Object of arrays (columns).
        csv = await ConvertTextAsync("columns.yaml", "name: [a, b]\nage: [1]\n", "csv");
        Assert.Equal("name,age\r\na,1\r\nb,\r\n", csv);

        // XML rows.
        csv = await ConvertTextAsync("rows.xml", "<rows><row><a>1</a><b>x</b></row><row><a>2</a><b>y</b></row></rows>", "csv");
        Assert.Equal("a,b\r\n1,x\r\n2,y\r\n", csv);

        var error = await FailAsync("nested.json", """[{"id": 1, "tags": ["a", "b"]}]""", "csv");
        Assert.Contains("“tags” holds a list", error);
        Assert.Contains("CSV holds a table", error);
        error = await FailAsync("deep.json", """[{"a": {"b": {"c": 1}}}]""", "csv");
        Assert.Contains("“a.b” is nested more than one level", error);
        error = await FailAsync("value.json", "42", "csv");
        Assert.Contains("single value", error);
    }

    [Fact]
    public async Task Json_lines_both_ways()
    {
        var json = await ConvertTextAsync("events.jsonl", "{\"a\":1}\n\n{\"a\":2,\"b\":\"한\"}\n", "json");
        Assert.Equal("[\n  {\n    \"a\": 1\n  },\n  {\n    \"a\": 2,\n    \"b\": \"한\"\n  }\n]\n", json);

        var lines = await ConvertTextAsync("events.json", json, "jsonl");
        Assert.Equal("{\"a\":1}\n{\"a\":2,\"b\":\"한\"}\n", lines);

        var error = await FailAsync("broken.jsonl", "{\"a\":1}\n{oops}\n", "json");
        Assert.Contains("Line 2", error);
    }

    [Fact]
    public async Task Spreadsheets_and_contacts_reach_json_through_csv()
    {
        var planner = fx.Catalog.CreatePlanner();
        Assert.Equal(["spreadsheet", "data"], planner.Plan("xlsx", "json")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["data", "spreadsheet"], planner.Plan("yaml", "xlsx")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["data", "data"], planner.Plan("vcf", "json")!.Steps.Select(s => s.Converter.Id));
        Assert.Single(planner.Plan("csv", "json")!.Steps);

        var xlsx = await ConvertAsync(Write("sheet.csv", "item,qty\r\n사과,3\r\n"), "xlsx");
        var json = await File.ReadAllTextAsync(await ConvertAsync(xlsx, "json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"item\": \"사과\",\n    \"qty\": 3", json);
    }
}
