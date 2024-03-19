using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using System;

namespace MovieTheater.Gql.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class HotChocolateQueryAttribute : ObjectTypeDescriptorAttribute
    {
        protected override void OnConfigure(IDescriptorContext context, IObjectTypeDescriptor descriptor, Type type)
        {
            descriptor.ExtendsType(typeof(Query));
        }
    }
}
