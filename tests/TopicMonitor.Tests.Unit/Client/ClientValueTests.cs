using TopicMonitor.Client;
using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Client;

public class ClientValueTests
{
    [Fact]
    public void FromProto_maps_bool()
    {
        var v = ClientValue.FromProto(new TopicValue { B = true });
        v.Kind.ShouldBe(ClientValueKind.Bool);
        v.AsBool.ShouldBeTrue();
    }

    [Fact]
    public void FromProto_maps_int()
    {
        var v = ClientValue.FromProto(new TopicValue { I = 42 });
        v.Kind.ShouldBe(ClientValueKind.Int);
        v.AsInt.ShouldBe(42);
    }

    [Fact]
    public void FromProto_maps_float()
    {
        var v = ClientValue.FromProto(new TopicValue { D = 3.5 });
        v.Kind.ShouldBe(ClientValueKind.Float);
        v.AsFloat.ShouldBe(3.5);
    }

    [Fact]
    public void FromProto_maps_string()
    {
        var v = ClientValue.FromProto(new TopicValue { S = "READY" });
        v.Kind.ShouldBe(ClientValueKind.String);
        v.AsString.ShouldBe("READY");
    }

    [Fact]
    public void FromProto_maps_enum_index()
    {
        var v = ClientValue.FromProto(new TopicValue { EnumIndex = 3 });
        v.Kind.ShouldBe(ClientValueKind.Enum);
        v.AsEnumIndex.ShouldBe(3u);
    }

    [Fact]
    public void FromProto_maps_vec_preserving_component_order()
    {
        var tv = new TopicValue { Vec = new Vec { Values = { 240, 20, 20 } } };
        var v = ClientValue.FromProto(tv);
        v.Kind.ShouldBe(ClientValueKind.Vec);
        v.AsVec.ShouldBe(new[] { 240.0, 20.0, 20.0 });
    }

    [Fact]
    public void FromProto_maps_unset_oneof_to_None()
    {
        var v = ClientValue.FromProto(new TopicValue());
        v.Kind.ShouldBe(ClientValueKind.None);
    }

    [Fact]
    public void Accessing_the_wrong_kind_throws()
    {
        var v = ClientValue.OfInt(1);
        Should.Throw<InvalidOperationException>(() => v.AsString);
    }

    [Fact]
    public void Equality_is_by_kind_and_payload()
    {
        ClientValue.OfInt(5).ShouldBe(ClientValue.OfInt(5));
        ClientValue.OfInt(5).ShouldNotBe(ClientValue.OfInt(6));
        ClientValue.OfVec(new[] { 1.0, 2.0 }).ShouldBe(ClientValue.OfVec(new[] { 1.0, 2.0 }));
    }
}
