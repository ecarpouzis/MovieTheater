using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The EF model IS the approved mapping (docs/books/v2-mapping.json): same tables in the same file, same
    /// columns, same primary keys. The entities are generated from the mapping, so this is the tripwire for
    /// someone editing the C# by hand — or the mapping without regenerating.
    /// </summary>
    public class ModelMatchesMappingTests
    {
        private static readonly MappingContract Mapping = MappingContract.Load();

        [Fact]
        public void HotContextTablesEqualTheMappingsHotCatalog()
        {
            using var db = new BooksDb(BooksDbOptions.Hot(":memory:"));
            AssertModelEquals(db, "hot", except: ItemFts.Table);
        }

        [Fact]
        public void LegsContextTablesEqualTheMappingsLegsCatalog()
        {
            using var db = new BooksLegsDb(BooksDbOptions.Legs(":memory:"));
            AssertModelEquals(db, "legs");
        }

        [Fact]
        public void EveryEnumInTheMappingHasACSharpTwinWithTheSameMembersInOrder()
        {
            var enums = typeof(ItemKind).Assembly.GetTypes().Where(t => t.IsEnum).ToDictionary(t => t.Name);
            var byMembers = enums.ToDictionary(kv => string.Join(",", Enum.GetNames(kv.Value)), kv => kv.Key);
            foreach (var (key, members) in Mapping.Enums)
            {
                var joined = string.Join(",", members);
                Assert.True(byMembers.ContainsKey(joined), $"mapping enum '{key}' ({joined}) has no C# enum with exactly those members in that order");
            }
        }

        [Fact]
        public void StageListIsTheMappingsAndEveryV1TableWithTargetsHasAStage()
        {
            Assert.Equal(30, Mapping.Stages.Count);
            foreach (var t in Mapping.V1.Values.Where(t => t.Targets.Count > 0))
                Assert.Contains(t.Stage, Mapping.Stages);
        }

        private static void AssertModelEquals(DbContext db, string file, string? except = null)
        {
            var expected = Mapping.TablesIn(file).Where(t => t.Name != except).ToDictionary(t => t.Name);
            var actual = db.Model.GetEntityTypes().ToDictionary(e => e.GetTableName()!);

            Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
            foreach (var (name, table) in expected)
            {
                var entity = actual[name];
                var cols = entity.GetProperties().Select(p => p.GetColumnName()).OrderBy(c => c).ToList();
                Assert.Equal(table.Columns.Select(c => c.Name).OrderBy(c => c).ToList(), cols);
                var pk = entity.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()).ToList();
                Assert.Equal(table.PrimaryKey, pk);
            }
        }
    }
}
