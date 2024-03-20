// https://github.com/jasonliang-dev/entity-component-system
using System.Diagnostics;
using System;
using TinyEcs;
using System.Runtime.CompilerServices;

const int ENTITIES_COUNT = (524_288 * 2 * 1);


// Program2.Test();
//BevySystems2.Program3.Test();



using var ecs = new World();

ecs.Entity<PlayerTag>();
ecs.Entity<Likes>();
ecs.Entity<Position>();
ecs.Entity<Velocity>();

var scheduler = new Scheduler(ecs);
scheduler
	.AddSystem(
		(
			Query<(Position, Velocity), (Not<PlayerTag>, Not<Likes>)> query0,
			Query<Position> query1,
			Res<string> myText
		) =>
		{
			query0.Each((ref Position pos, ref Velocity vel) => {
				pos.X *= vel.X;
				pos.Y *= vel.Y;
			});

			query1.Each((ref Position pos) => { });

			Console.WriteLine("What: {0}", myText.Value);
		}
	)
	.AddSystem((Commands commands) => {
		commands.Entity()
			.Set<Position>(default)
			.Set<Velocity>(default);
	})
	.AddSystem((Commands commands) => {
		commands.Merge();
	})
	.AddSystem((World world) => {
		Console.WriteLine("entities in world {0}", world.EntityCount);
	})
	.AddSystem((ComplexQuery complex) => {
		Console.WriteLine();
	})
	.AddResource("oh shit i made it");

scheduler.Run();


// var e = ecs.Entity("Main")
// 	.Set<Position>(new Position() {X = 2})
// 	.Set<Velocity>(new Velocity());



for (int i = 0; i < ENTITIES_COUNT / 1; i++)
	ecs.Entity()
		.Set<Position>(new Position() {X = i})
		.Set<Velocity>(new Velocity(){X = i})
		//  .Set<PlayerTag>()
		//  .Set<Dogs>()
		//  .Set<Likes>()
		 ;

// for (var i = 7000; i < 8000 * 2; ++i)
// 	ecs.Entity((ulong)i).Delete();


var sw = Stopwatch.StartNew();
var start = 0f;
var last = 0f;

while (true)
{
	for (int i = 0; i < 3600; ++i)
	{
		scheduler.Run();
	}

	last = start;
	start = sw.ElapsedMilliseconds;

	Console.WriteLine("query done in {0} ms", start - last);
}




enum TileType
{
	Land,
	Static
}


struct Serial { public uint Value; }
struct Position { public float X, Y, Z; }
struct Velocity { public float X, Y; }
struct PlayerTag { }

struct CustomEvent { }

struct Likes;
struct Dogs { }
struct Apples { }

struct TestStr { public byte v; }

struct ManagedData { public string Text; public int Integer; }

struct Context1 {}
struct Context2 {}

struct Chunk;
struct ChunkTile;


struct ComplexQuery : ISystemParam
{
	public Query<(Position, Velocity)> Q0;
	public Query<(Position, Velocity), (With<PlayerTag>, Not<Likes>)> Q1;

	void ISystemParam.New(object arguments)
	{
		var world = (World) arguments;
		Q0 = world.Query<(Position, Velocity)>();
		Q1 = world.Query<(Position, Velocity), (With<PlayerTag>, Not<Likes>)>();
	}
}
