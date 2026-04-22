using Grasshopper.Kernel.Types;

namespace GenericMongoPlugin.Types;

public sealed class MongoFilterEntryGoo : GH_Goo<MongoFilterEntry>
{
    public MongoFilterEntryGoo() { }

    public MongoFilterEntryGoo(MongoFilterEntry native) : base(native) { }

    public override bool IsValid => Value != null && !string.IsNullOrWhiteSpace(Value.Key);

    public override string TypeName => "MongoFilterEntry";

    public override string TypeDescription => "A single filter entry (key/value) used to build MongoDB queries";

    public override IGH_Goo Duplicate() => Value == null ? new MongoFilterEntryGoo() : new MongoFilterEntryGoo(new MongoFilterEntry(Value.Key, Value.Value));

    public override string ToString() => Value == null ? "<null filter entry>" : $"{Value.Key}: {Value.Value}";

    public override object ScriptVariable() => Value;
}
