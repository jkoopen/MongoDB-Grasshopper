using Grasshopper.Kernel.Types;

namespace GenericMongoPlugin.Types;

public sealed class MongoFilterGoo : GH_Goo<MongoFilter>
{
    public MongoFilterGoo() { }

    public MongoFilterGoo(MongoFilter native) : base(native) { }

    public override bool IsValid => Value != null;

    public override string TypeName => "MongoFilter";

    public override string TypeDescription => "A MongoDB filter";

    public override IGH_Goo Duplicate() => Value == null ? new MongoFilterGoo() : new MongoFilterGoo(Value.Clone());

    public override string ToString() => Value == null ? "<null filter>" : Value.ToString();

    public override object ScriptVariable() => Value;
}
