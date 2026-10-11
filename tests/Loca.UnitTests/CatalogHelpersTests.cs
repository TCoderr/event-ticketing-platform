using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Identity;

namespace Loca.UnitTests;

public sealed class LayoutGeneratorTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    public void Row_labels_continue_after_z(int index, string expected) => Assert.Equal(expected, LayoutGenerator.RowLabel(index));

    [Fact]
    public void Seats_are_generated_per_section_with_unique_positions_and_centered_narrow_sections()
    {
        var seats = LayoutGenerator.Generate(
        [
            new LayoutSectionRequest("Salon", "A", Rows: 2, SeatsPerRow: 6, AisleAfter: 3),
            new LayoutSectionRequest("Balkon", "B", Rows: 1, SeatsPerRow: 4, AisleAfter: null),
        ]);

        Assert.Equal(16, seats.Count);
        Assert.Equal(seats.Count, seats.Select(s => (s.X, s.Y)).Distinct().Count());
        Assert.Equal(seats.Count, seats.Select(s => (s.Section, s.RowLabel, s.SeatNumber)).Distinct().Count());
        Assert.All(seats.Where(s => s.Section == "Salon"), s => Assert.Equal("A", s.TierCode));
        Assert.All(seats.Where(s => s.Section == "Balkon"), s => Assert.Equal("B", s.TierCode));

        // Koridor: 3. koltuktan sonra bir koltuk genişliğinde boşluk (onda bir koltuk birimi → 10).
        var firstRow = seats.Where(s => s.Section == "Salon" && s.RowLabel == "A").OrderBy(s => s.SeatNumber).ToList();
        Assert.Equal(10, firstRow[1].X - firstRow[0].X);
        Assert.Equal(20, firstRow[3].X - firstRow[2].X);

        // Dar bölüm ortalanır ve bölümler arasında boşluk bırakılır.
        var balcony = seats.Where(s => s.Section == "Balkon").OrderBy(s => s.SeatNumber).ToList();
        Assert.Equal((7 - 4) * 10 / 2, balcony[0].X);
        Assert.True(balcony[0].Y > firstRow[0].Y + 10 * 2);
    }
}

public sealed class SlugTests
{
    [Theory]
    [InlineData("Sis Vadisi Senfonisi", "sis-vadisi-senfonisi")]
    [InlineData("Çağdaş İstanbul Şöleni: Öğle & Üç Işık", "cagdas-istanbul-soleni-ogle-uc-isik")]
    [InlineData("   --  ", "x")]
    public void Turkish_characters_are_transliterated_and_separators_collapsed(string input, string expected) =>
        Assert.Equal(expected, Slugs.Create(input, withSuffix: false));

    [Fact]
    public void Suffix_makes_slugs_unique_and_keeps_them_url_safe()
    {
        var slugs = Enumerable.Range(0, 200).Select(_ => Slugs.Create("Aynı Başlık")).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
        Assert.All(slugs, s => Assert.Matches("^ayni-baslik-[0-9a-f]{6}$", s));
    }
}
