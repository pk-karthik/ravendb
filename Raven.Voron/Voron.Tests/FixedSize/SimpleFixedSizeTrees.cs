﻿// -----------------------------------------------------------------------
//  <copyright file="SimpleFixedSizeTrees.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Text;
using Voron.Trees.Fixed;
using Xunit;

namespace Voron.Tests.FixedSize
{
	public class SimpleFixedSizeTrees : StorageTest
	{
		

		[Fact]
		public void TimeSeries()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("watches/12831-12345", valSize: 8);

				fst.Add(DateTime.Today.AddHours(8).Ticks, new Slice(BitConverter.GetBytes(80D)));
				fst.Add(DateTime.Today.AddHours(9).Ticks, new Slice(BitConverter.GetBytes(65D)));
				fst.Add(DateTime.Today.AddHours(10).Ticks, new Slice(BitConverter.GetBytes(44D)));

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				var fst = tx.State.Root.FixedTreeFor("watches/12831-12345", valSize:8);

				var it = fst.Iterate();
				Assert.True(it.Seek(DateTime.Today.AddHours(7).Ticks));
				var buffer = new byte[8];
				it.Value.CopyTo(buffer);
				Assert.Equal(80D, BitConverter.ToDouble(buffer,0));
				Assert.True(it.MoveNext());
				it.Value.CopyTo(buffer);
				Assert.Equal(65D, BitConverter.ToDouble(buffer, 0));
				Assert.True(it.MoveNext());
				it.Value.CopyTo(buffer);
				Assert.Equal(44d, BitConverter.ToDouble(buffer, 0));
				Assert.False(it.MoveNext());

				tx.Commit();
			}
		}
		[Fact]
		public void CanAdd()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				fst.Add(1);
				fst.Add(2);

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				Assert.True(fst.Contains(1));
				Assert.True(fst.Contains(2));
				Assert.False(fst.Contains(3));
				tx.Commit();
			}
		}

		[Fact]
		public void CanAdd_Mixed()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				fst.Add(2);
				fst.Add(6);
				fst.Add(1);
				fst.Add(3);
				fst.Add(-3);

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				Assert.True(fst.Contains(1));
				Assert.True(fst.Contains(2));
				Assert.False(fst.Contains(5));
				Assert.True(fst.Contains(6));
				Assert.False(fst.Contains(4));
				Assert.True(fst.Contains(-3));
				Assert.True(fst.Contains(3));
				tx.Commit();
			}
		}

		[Fact]
		public void CanIterate()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				fst.Add(3);
				fst.Add(1);
				fst.Add(2);

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				var it = fst.Iterate();
				Assert.True(it.Seek(long.MinValue));
				Assert.Equal(1L, it.Key);
				Assert.True(it.MoveNext());
				Assert.Equal(2L, it.Key);
				Assert.True(it.MoveNext());
				Assert.Equal(3L, it.Key);
				Assert.False(it.MoveNext());


				tx.Commit();
			}
		}


		[Fact]
		public void CanRemove()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				fst.Add(1);
				fst.Add(2);
				fst.Add(3);
				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				fst.Delete(2);

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				var fst = tx.State.Root.FixedTreeFor("test");

				Assert.True(fst.Contains(1));
				Assert.False(fst.Contains(2));
				Assert.True(fst.Contains(3));
				tx.Commit();
			}
		}

		[Fact]
		public void CanAdd_WithValue()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test", 8);

				fst.Add(1, new Slice(BitConverter.GetBytes(1L)));
				fst.Add(2, new Slice(BitConverter.GetBytes(2L)));

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				var fst = tx.State.Root.FixedTreeFor("test", 8);

				Assert.Equal(1L, fst.Read(1).CreateReader().ReadLittleEndianInt64());
				Assert.Equal(2L, fst.Read(2).CreateReader().ReadLittleEndianInt64());
				Assert.Null(fst.Read(3));
				tx.Commit();
			}
		}

		[Fact]
		public void CanRemove_WithValue()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test", 8);

				fst.Add(1, new Slice(BitConverter.GetBytes(1L)));
				fst.Add(2, new Slice(BitConverter.GetBytes(2L)));
				fst.Add(3, new Slice(BitConverter.GetBytes(3L)));

				tx.Commit();
			}


			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var fst = tx.State.Root.FixedTreeFor("test", 8);

				fst.Delete(2);

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				var fst = tx.State.Root.FixedTreeFor("test", 8);

				Assert.Equal(1L, fst.Read(1).CreateReader().ReadLittleEndianInt64());
				Assert.Null(fst.Read(2));
				Assert.Equal(3L, fst.Read(3).CreateReader().ReadLittleEndianInt64());
				tx.Commit();
			}
		}
	}
}