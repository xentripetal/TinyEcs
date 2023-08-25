﻿namespace TinyEcs.Tests
{
    public class QueryTest
    {
        [Theory]
        [InlineData(1)]
        [InlineData(1000)]
        [InlineData(1_000_000)]
        public void Query_AttachOneComponent_WithOneComponent<TContext>(int amount)
        {
            using var world = new World<TContext>();

            for (int i = 0; i < amount; i++)
                world.Set<FloatComponent>(world.New());

            var query = world.Query()
                .With<FloatComponent>();

            int done = 0;
			query.Iterate((ref Iterator<TContext> it) => done += it.Count);

            Assert.Equal(amount, done);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1000)]
        [InlineData(1_000_000)]
        public void Query_AttachTwoComponents_WithTwoComponents<TContext>(int amount)
        {
            using var world = new World<TContext>();

            for (int i = 0; i < amount; i++)
            {
                var e = world.New();
                world.Set<FloatComponent>(e);
                world.Set<IntComponent>(e);
            }

            var query = world.Query()
                .With<FloatComponent>()
                .With<IntComponent>();

            int done = 0;
            query.Iterate((ref Iterator<TContext> it) => done += it.Count);

            Assert.Equal(amount, done);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1000)]
        [InlineData(1_000_000)]
        public void Query_AttachThreeComponents_WithThreeComponents<TContext>(int amount)
        {
            using var world = new World<TContext>();

            for (int i = 0; i < amount; i++)
            {
                var e = world.New();
                world.Set<FloatComponent>(e);
                world.Set<IntComponent>(e);
                world.Set<BoolComponent>(e);
            }

            var query = world.Query()
                .With<FloatComponent>()
                .With<IntComponent>()
                .With<BoolComponent>();

            int done = 0;
            query.Iterate((ref Iterator<TContext> it) => done += it.Count);

            Assert.Equal(amount, done);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1000)]
        [InlineData(1_000_000)]
        public void Query_AttachThreeComponents_WithTwoComponents_WithoutOneComponent<TContext>(int amount)
        {
            using var world = new World<TContext>();

            for (int i = 0; i < amount; i++)
            {
                var e = world.New();
                world.Set<FloatComponent>(e);
                world.Set<IntComponent>(e);
                world.Set<BoolComponent>(e);
            }

            var query = world.Query()
                .With<FloatComponent>()
                .With<IntComponent>()
                .Without<BoolComponent>();

            int done = 0;
            query.Iterate((ref Iterator<TContext> it) => done += it.Count);

            Assert.Equal(0, done);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1000)]
        [InlineData(1_000_000)]
        public void Query_AttachTwoComponents_WithTwoComponents_WithoutOneComponent<TContext>(int amount)
        {
            using var world = new World<TContext>();

            for (int i = 0; i < amount; i++)
            {
                var e = world.New();
                world.Set<FloatComponent>(e);
                world.Set<IntComponent>(e);
            }

            var query = world.Query()
                .With<FloatComponent>()
                .With<IntComponent>()
                .Without<BoolComponent>();

            int done = 0;
            query.Iterate((ref Iterator<TContext> it) => done += it.Count);

            Assert.Equal(amount, done);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1000)]
        [InlineData(1_000_000)]
        public void Query_AttachTwoComponents_WithOneComponents_WithoutTwoComponent<TContext>(int amount)
        {
            using var world = new World<TContext>();

            for (int i = 0; i < amount; i++)
            {
                var e = world.New();
                world.Set<FloatComponent>(e);
                world.Set<IntComponent>(e);
            }

            var query = world.Query()
                .With<FloatComponent>()
                .Without<IntComponent>()
                .Without<BoolComponent>();

            int done = 0;
            query.Iterate((ref Iterator<TContext> it) => done += it.Count);

            Assert.Equal(0, done);
        }

        [Fact]
        public void Query_EdgeValidation<TContext>()
        {
            using var world = new World<TContext>();

            var good = 0;

            var e = world.New();
            world.Set<FloatComponent>(e);
            world.Set<IntComponent>(e);

            var e2 = world.New();
            world.Set<FloatComponent>(e2);
            world.Set<IntComponent>(e2);
            world.Set<BoolComponent>(e2);

            var e3 = world.New();
            world.Set<FloatComponent>(e3);
            world.Set<IntComponent>(e3);
            world.Set<BoolComponent>(e3);
            good++;

            var e4 = world.New();
            world.Set<FloatComponent>(e4);
            world.Set<IntComponent>(e4);
            world.Set<BoolComponent>(e4);
            world.Set<NormalTag>(e4);

            var query = world.Query()
                .With<FloatComponent>()
                .With<IntComponent>()
                .Without<BoolComponent>()
                .Without<NormalTag>();

            int done = 0;
            query.Iterate((ref Iterator<TContext> it) => done += it.Count);

            Assert.Equal(good, done);
        }
    }
}
