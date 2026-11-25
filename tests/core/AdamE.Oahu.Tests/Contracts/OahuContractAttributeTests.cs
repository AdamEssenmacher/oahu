using System.Reflection;
using Xunit;

namespace AdamE.Oahu.Tests.Contracts;

public sealed class OahuContractAttributeTests
{
    [Fact]
    public void The_attribute_marks_interfaces_only_once_each_and_is_not_inherited()
    {
        var usage = typeof(OahuContractAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Interface, usage.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
    }
}
