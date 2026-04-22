using Grasshopper.Kernel.Types;

namespace GenericMongoPlugin.Types;

public class MongoDbConnectionGoo : GH_Goo<MongoDbConnection>
{
    public MongoDbConnectionGoo() { }

    public MongoDbConnectionGoo(MongoDbConnection native) : base(native) { }

    public override bool IsValid => Value != null && Value.IsValid;

    public override string TypeName => "MongoDbConnection";

    public override string TypeDescription => "A connection definition for a MongoDB database";

    public override IGH_Goo Duplicate() => new MongoDbConnectionGoo(Value);

    public override string ToString() => $"MongoDB DB: {Value?.DatabaseName}";

    public override object ScriptVariable() => Value;
}
