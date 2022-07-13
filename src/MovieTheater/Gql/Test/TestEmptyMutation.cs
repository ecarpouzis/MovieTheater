using HotChocolate.Types;
using MovieTheater.Gql.Attributes;

namespace MovieTheater.Gql.Test
{
    [HotChocolateMutation]
    public class TestEmptyMutation
    {
        public bool RunEmptyTest() => true;
    }

    [HotChocolateTypeExtension]
    public class TestMutationDescriptor : ObjectTypeExtension<TestEmptyMutation>
    {
        protected override void Configure(IObjectTypeDescriptor<TestEmptyMutation> descriptor)
        {
            descriptor.Field(x => x.RunEmptyTest());
        }
    }
}
