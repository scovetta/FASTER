﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using FASTER.core;
using System;
using System.IO;
using NUnit.Framework;

namespace FASTER.test.recovery.objects
{

    [TestFixture]
    public class ObjectRecoveryTest
    {
        static readonly int iterations = 21;
        string FasterFolderPath { get; set; }

        [SetUp]
        public void Setup()
        {
            FasterFolderPath = TestContext.CurrentContext.TestDirectory + "\\" + Path.GetRandomFileName();
            if (!Directory.Exists(FasterFolderPath))
                Directory.CreateDirectory(FasterFolderPath);
        }

        [TearDown]
        public void TearDown()
        {
            DeleteDirectory(FasterFolderPath);
        }

        public static void DeleteDirectory(string path)
        {
            foreach (string directory in Directory.GetDirectories(path))
            {
                DeleteDirectory(directory);
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                Directory.Delete(path, true);
            }
        }


        [Test]
        public void ObjectRecoveryTest1([Values]CheckpointType checkpointType)
        {

            Prepare(checkpointType, out string logPath, out string objPath, out IDevice log, out IDevice objlog, out FasterKV<MyKey, MyValue, MyInput, MyOutput, MyContext, MyFunctions> h, out MyContext context);

            h.StartSession();

            Write(h, context);

            h.Refresh();

            Read(h, context, false);

            h.TakeFullCheckpoint(out Guid CheckPointID);
            h.CompleteCheckpoint(true);

            Destroy(log, objlog, h);

            Prepare(checkpointType, out logPath, out objPath, out log, out objlog, out h, out context);

            h.Recover();

            h.StartSession();

            Read(h, context, true);

            Destroy(log, objlog, h);
        }

        private void Prepare(CheckpointType checkpointType, out string logPath, out string objPath, out IDevice log, out IDevice objlog, out FasterKV<MyKey, MyValue, MyInput, MyOutput, MyContext, MyFunctions> h, out MyContext context)
        {
            logPath = Path.Combine(FasterFolderPath, $"FasterRecoverTests.log");
            objPath = Path.Combine(FasterFolderPath, $"FasterRecoverTests_HEAP.log");
            log = Devices.CreateLogDevice(logPath);
            objlog = Devices.CreateLogDevice(objPath);
            h = new FasterKV
                <MyKey, MyValue, MyInput, MyOutput, MyContext, MyFunctions>
                (1L << 20, new MyFunctions(),
                new LogSettings
                {
                    LogDevice = log,
                    ObjectLogDevice = objlog,
                    SegmentSizeBits = 10,
                    MemorySizeBits = 10,
                    PageSizeBits = 9
                },
                new CheckpointSettings()
                {
                    CheckpointDir = Path.Combine(FasterFolderPath, "check-points"),
                    CheckPointType = checkpointType
                },
                new SerializerSettings<MyKey, MyValue> { keySerializer = () => new MyKeySerializer(), valueSerializer = () => new MyValueSerializer() }
             );
            context = new MyContext();
        }
        private static void Destroy(IDevice log, IDevice objlog, FasterKV<MyKey, MyValue, MyInput, MyOutput, MyContext, MyFunctions> h)
        {
            // Each thread ends session when done
            h.StopSession();

            // Dispose FASTER instance and log
            h.Dispose();
            log.Close();
            objlog.Close();
        }

        private void Write(FasterKV<MyKey, MyValue, MyInput, MyOutput, MyContext, MyFunctions> h, MyContext context)
        {
            for (int i = 0; i < iterations; i++)
            {
                var _key = new MyKey { key = i, name = i.ToString() };
                var value = new MyValue { value = i.ToString() };
                h.Upsert(ref _key, ref value, context, 0);
            }
        }

        private void Read(FasterKV<MyKey, MyValue, MyInput, MyOutput, MyContext, MyFunctions> h, MyContext context, bool delete)
        {
            var key = new MyKey { key = 1, name = "1" };
            var input = default(MyInput);
            MyOutput g1 = new MyOutput();
            var status = h.Read(ref key, ref input, ref g1, context, 0);

            if (status == Status.PENDING)
            {
                h.CompletePending(true);
                context.FinalizeRead(ref status, ref g1);
            }

            Assert.IsTrue(status == Status.OK);

            MyOutput g2 = new MyOutput();
            key = new MyKey { key = 2, name = "2" };
            status = h.Read(ref key, ref input, ref g2, context, 0);

            if (status == Status.PENDING)
            {
                h.CompletePending(true);
                context.FinalizeRead(ref status, ref g2);
            }

            Assert.IsTrue(status == Status.OK);

            if (delete)
            {
                var output = new MyOutput();
                h.Delete(ref key, context, 0);
                status = h.Read(ref key, ref input, ref output, context, 0);

                if (status == Status.PENDING)
                {
                    h.CompletePending(true);
                    context.FinalizeRead(ref status, ref output);
                }

                Assert.IsTrue(status == Status.NOTFOUND);
            }
        }
    }

    public class MyKeySerializer : BinaryObjectSerializer<MyKey>
    {
        public override void Serialize(ref MyKey key)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(key.name);
            writer.Write(4 + bytes.Length);
            writer.Write(key.key);
            writer.Write(bytes);
        }

        public override void Deserialize(ref MyKey key)
        {
            var size = reader.ReadInt32();
            key.key = reader.ReadInt32();
            var bytes = new byte[size - 4];
            reader.Read(bytes, 0, size - 4);
            key.name = System.Text.Encoding.UTF8.GetString(bytes);

        }
    }

    public class MyValueSerializer : BinaryObjectSerializer<MyValue>
    {
        public override void Serialize(ref MyValue value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value.value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public override void Deserialize(ref MyValue value)
        {
            var size = reader.ReadInt32();
            var bytes = new byte[size];
            reader.Read(bytes, 0, size);
            value.value = System.Text.Encoding.UTF8.GetString(bytes);
        }
    }

    public class MyKey : IFasterEqualityComparer<MyKey>
    {
        public int key;
        public string name;

        public long GetHashCode64(ref MyKey key) => Utility.GetHashCode(key.key);
        public bool Equals(ref MyKey key1, ref MyKey key2) => key1.key == key2.key && key1.name == key2.name;
    }


    public class MyValue { public string value; }
    public class MyInput { public string value; }
    public class MyOutput { public MyValue value; }

    public class MyContext
    {
        private Status _status;
        private MyOutput _g1;

        internal void Populate(ref Status status, ref MyOutput g1)
        {
            _status = status;
            _g1 = g1;
        }
        internal void FinalizeRead(ref Status status, ref MyOutput g1)
        {
            status = _status;
            g1 = _g1;
        }
    }


    public class MyFunctions : IFunctions<MyKey, MyValue, MyInput, MyOutput, MyContext>
    {
        public void InitialUpdater(ref MyKey key, ref MyInput input, ref MyValue value) => value.value = input.value;
        public void CopyUpdater(ref MyKey key, ref MyInput input, ref MyValue oldValue, ref MyValue newValue) => newValue = oldValue;
        public bool InPlaceUpdater(ref MyKey key, ref MyInput input, ref MyValue value)
        {
            if (value.value.Length < input.value.Length)
                return false;
            value.value = input.value;
            return true;
        }


        public void SingleReader(ref MyKey key, ref MyInput input, ref MyValue value, ref MyOutput dst) => dst.value = value;
        public void SingleWriter(ref MyKey key, ref MyValue src, ref MyValue dst) => dst = src;
        public void ConcurrentReader(ref MyKey key, ref MyInput input, ref MyValue value, ref MyOutput dst) => dst.value = value;
        public bool ConcurrentWriter(ref MyKey key, ref MyValue src, ref MyValue dst)
        {
            if (src == null)
                return false;

            if (dst.value.Length != src.value.Length)
                return false;

            dst = src;
            return true;
        }

        public void ReadCompletionCallback(ref MyKey key, ref MyInput input, ref MyOutput output, MyContext ctx, Status status) => ctx.Populate(ref status, ref output);
        public void UpsertCompletionCallback(ref MyKey key, ref MyValue value, MyContext ctx) { }
        public void RMWCompletionCallback(ref MyKey key, ref MyInput input, MyContext ctx, Status status) { }
        public void DeleteCompletionCallback(ref MyKey key, MyContext ctx) { }
        public void CheckpointCompletionCallback(Guid sessionId, long serialNum) { }
    }
}
