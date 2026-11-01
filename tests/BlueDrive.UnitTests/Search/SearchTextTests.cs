using BlueDrive.Application.Features.Search;
using FluentAssertions;

namespace BlueDrive.UnitTests.Search;

public class SearchTextTests
{
    [Fact]
    public void Terms_are_unaccented_tokens() => SearchText.Terms("Dân số, KHHGĐ 2025!").Should().Equal("dan", "so", "khhgd", "2025");

    [Fact]
    public void Highlight_marks_original_text_accent_insensitively()
        => SearchText.Highlight("Báo cáo Dân số năm 2025", SearchText.Terms("dan so"))
            .Should().Be("Báo cáo <mark>Dân</mark> <mark>số</mark> năm 2025");

    [Fact]
    public void Highlight_encodes_html() => SearchText.Highlight("<b>dân</b>", ["dan"]).Should().Be("&lt;b&gt;<mark>dân</mark>&lt;/b&gt;");

    [Fact]
    public void Snippet_centers_on_the_first_match()
    {
        var text = new string('x', 300) + " kế hoạch hóa gia đình " + new string('y', 300);
        var s = SearchText.Snippet(text, ["ke", "hoach"]);
        s.Should().StartWith("…").And.Contain("<mark>kế</mark> <mark>hoạch</mark>").And.EndWith("…");
    }
}
