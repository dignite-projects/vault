using System.Collections.Generic;
using System.Linq;
using Shouldly;
using UglyToad.PdfPig.Core;
using Xunit;

namespace Dignite.Vault.Parse.Pdf;

public class PdfTableContinuation_Tests
{
    private static readonly PdfRectangle Box = new(50, 100, 300, 700);

    private static PdfTableBlock Table(params string[][] rows) => new(Box, PdfTable.FromRows(rows));

    private static PdfParagraphBlock Paragraph(string text) => new(Box, text);

    private static IEnumerable<string[]> Rows(PdfBlock block)
    {
        var table = ((PdfTableBlock)block).Table;
        return Enumerable.Range(0, table.RowCount)
            .Select(row => table.Cells.Where(cell => cell.Row == row).OrderBy(cell => cell.Column).Select(cell => cell.Text).ToArray());
    }

    [Fact]
    public void Appends_the_table_that_starts_the_next_page_and_drops_its_repeated_header()
    {
        var first = new PdfTableContinuation.Page(1, [Paragraph("intro"), Table(["Item", "Price"], ["A", "1"])]);
        var second = new PdfTableContinuation.Page(2, [Table(["Item", "Price"], ["B", "2"]), Paragraph("note")]);

        PdfTableContinuation.Join([first, second]);

        Rows(first.Blocks[^1]).ShouldBe([["Item", "Price"], ["A", "1"], ["B", "2"]]);
        ((PdfTableBlock)first.Blocks[^1]).Continuations.ShouldHaveSingleItem().PageNumber.ShouldBe(2);
        second.Blocks.ShouldHaveSingleItem().ShouldBeOfType<PdfParagraphBlock>();
        ((PdfTableBlock)first.Blocks[^1]).Table.Cells.Where(cell => cell.Text == "B").ShouldHaveSingleItem().PageNumber.ShouldBe(2);
    }

    [Fact]
    public void Keeps_a_continuation_without_a_repeated_header_as_rows()
    {
        var first = new PdfTableContinuation.Page(1, [Table(["Item", "Price"], ["A", "1"])]);
        var second = new PdfTableContinuation.Page(2, [Table(["B", "2"], ["C", "3"])]);

        PdfTableContinuation.Join([first, second]);

        Rows(first.Blocks[^1]).ShouldBe([["Item", "Price"], ["A", "1"], ["B", "2"], ["C", "3"]]);
        second.Blocks.ShouldBeEmpty();
    }

    [Fact]
    public void Follows_a_table_across_a_page_it_fills()
    {
        var first = new PdfTableContinuation.Page(1, [Table(["Item", "Price"], ["A", "1"])]);
        var second = new PdfTableContinuation.Page(2, [Table(["B", "2"])]);
        var third = new PdfTableContinuation.Page(3, [Table(["C", "3"]), Paragraph("end")]);

        PdfTableContinuation.Join([first, second, third]);

        Rows(first.Blocks[^1]).ShouldBe([["Item", "Price"], ["A", "1"], ["B", "2"], ["C", "3"]]);
        ((PdfTableBlock)first.Blocks[^1]).Continuations.Select(c => c.PageNumber).ShouldBe([2, 3]);
        third.Blocks.ShouldHaveSingleItem().ShouldBeOfType<PdfParagraphBlock>();
    }

    [Fact]
    public void Leaves_apart_a_table_that_starts_the_next_page_under_a_header_of_its_own()
    {
        // Same columns, back to back across the break, but the second opens with a different header: another table.
        var first = new PdfTableContinuation.Page(1, [Table(["Item", "Price"], ["Paper", "120"], ["Pens", "80"])]);
        var second = new PdfTableContinuation.Page(2, [Table(["Name", "Age"], ["Sato", "34"], ["Suzuki", "41"])]);

        PdfTableContinuation.Join([first, second]);

        Rows(first.Blocks[^1]).ShouldBe([["Item", "Price"], ["Paper", "120"], ["Pens", "80"]]);
        Rows(second.Blocks.ShouldHaveSingleItem()).ShouldBe([["Name", "Age"], ["Sato", "34"], ["Suzuki", "41"]]);
    }

    [Fact]
    public void Joins_a_table_of_text_whose_continuation_opens_on_a_row_like_its_header()
    {
        // A roster cut by the break: the next page's first row is text over text, as the header is. Nothing tells it from
        // a header, so it is read as the rows it most likely is.
        var first = new PdfTableContinuation.Page(1, [Table(["Name", "Role"], ["Sato", "Lead"], ["Kato", "Chair"])]);
        var second = new PdfTableContinuation.Page(2, [Table(["Suzuki", "Member"], ["Tanaka", "Member"])]);

        PdfTableContinuation.Join([first, second]);

        Rows(first.Blocks[^1]).ShouldBe([["Name", "Role"], ["Sato", "Lead"], ["Kato", "Chair"], ["Suzuki", "Member"], ["Tanaka", "Member"]]);
        second.Blocks.ShouldBeEmpty();
    }

    [Fact]
    public void Leaves_tables_apart_when_something_comes_between_or_the_columns_differ()
    {
        var first = new PdfTableContinuation.Page(1, [Table(["Item", "Price"], ["A", "1"]), Paragraph("total")]);
        var second = new PdfTableContinuation.Page(2, [Table(["Item", "Price"], ["B", "2"])]);
        var third = new PdfTableContinuation.Page(3, [Table(["X", "Y", "Z"], ["1", "2", "3"])]);

        PdfTableContinuation.Join([first, second, third]);

        first.Blocks.Count.ShouldBe(2);
        second.Blocks.ShouldHaveSingleItem();
        third.Blocks.ShouldHaveSingleItem();
    }
}
