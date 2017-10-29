﻿// ***********************************************************************
// Copyright (c) 2017 Charlie Poole, Rob Prouse
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

#if PARALLEL
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework.Interfaces;
using NUnit.TestData.ParallelExecutionData;
using NUnit.TestUtilities;

namespace NUnit.Framework.Internal.Execution
{
    [TestFixtureSource(nameof(GetParallelSuites))]
    [NonParallelizable]
    public class ParallelExecutionTests : ITestListener
    {
        private readonly TestSuite _testSuite;
        private readonly Expectations _expectations;

        private ConcurrentQueue<TestEvent> _events;
        private TestResult _result;

        private IEnumerable<TestEvent> AllEvents { get { return _events.AsEnumerable();  } }    
        private IEnumerable<TestEvent> ShiftEvents {  get { return AllEvents.Where(e => e.Action == TestAction.ShiftStarted || e.Action == TestAction.ShiftFinished);  } }
        private IEnumerable<TestEvent> TestEvents {  get { return AllEvents.Where(e => e.Action == TestAction.TestStarting || e.Action == TestAction.TestFinished); } }

        public ParallelExecutionTests(TestSuite testSuite)
        {
            _testSuite = testSuite;
        }

        public ParallelExecutionTests(TestSuite testSuite, Expectations expectations)
        {
            _testSuite = testSuite;
            _expectations = expectations;
        }
        
        [OneTimeSetUp]
        public void RunTestSuite()
        {
            _events = new ConcurrentQueue<TestEvent>();

            var dispatcher = new ParallelWorkItemDispatcher(4);
            var context = new TestExecutionContext();
            context.Dispatcher = dispatcher;
            context.Listener = this;
            
            dispatcher.ShiftStarting += (shift) =>
            {
                _events.Enqueue(new TestEvent()
                {
                    Action = TestAction.ShiftStarted,
                    ShiftName = shift.Name
                });
            };

            dispatcher.ShiftFinished += (shift) =>
            {
                _events.Enqueue(new TestEvent()
                {
                    Action = TestAction.ShiftFinished,
                    ShiftName = shift.Name
                });
            };

            var workItem = TestBuilder.CreateWorkItem(_testSuite, context);

            dispatcher.Start(workItem);
            workItem.WaitForCompletion();

            _result = workItem.Result;
        }


        // NOTE: The following tests use Assert.Fail under control of an
        // if statement to avoid evaluating DumpEvents unnecessarily.
        // Unfortunately, we can't use the form of Assert that takes
        // a Func for a message because it's not present in .NET 2.0

        [Test]
        public void AllTestsPassed()
        {
            if (_result.ResultState != ResultState.Success || _result.PassCount != _testSuite.TestCaseCount)
                Assert.Fail(DumpEvents("Not all tests passed"));
        }

        [Test]
        public void OnlyOneShiftIsActiveAtSameTime()
        {
            int count = 0;
            foreach (var e in _events)
            {
                if (e.Action == TestAction.ShiftStarted && ++count > 1)
                    Assert.Fail(DumpEvents("Shift started while another shift was active"));

                if (e.Action == TestAction.ShiftFinished)
                    --count;
            }           
        }

        [Test]
        public void CorrectInitialShift()
        {
            string expected = "NonParallel";
            if (_testSuite.Properties.ContainsKey(PropertyNames.ParallelScope))
            {
                var scope = (ParallelScope)_testSuite.Properties.Get(PropertyNames.ParallelScope);
                if ((scope & ParallelScope.Self) != 0)
                    expected = "Parallel";
            }

            var e = _events.First();
            Assert.That(e.Action, Is.EqualTo(TestAction.ShiftStarted));
            Assert.That(e.ShiftName, Is.EqualTo(expected));
        }

        [Test]
        public void TestsRunOnExpectedWorkers()
        {
            Assert.Multiple(() =>
            {
                foreach (var e in TestEvents)
                    _expectations.Verify(e);
            });
        }

        #region Test Data

        static IEnumerable<TestFixtureData> GetParallelSuites()
        {
            yield return new TestFixtureData(
                Suite("fake-assembly.dll")
                    .Containing(Suite("NUnit")
                        .Containing(Suite("Tests")
                            .Containing(Fixture(typeof(TestFixture1))))),
                Expecting(
                    That("fake-assembly.dll").RunsOn("NonParallelWorker"),
                    That("NUnit").RunsOn("NonParallelWorker"),
                    That("Tests").RunsOn("NonParallelWorker"),
                    That("TestFixture1").RunsOn("NonParallelWorker"),
                    That("TestFixture1_Test").RunsOn("NonParallelWorker")))
                .SetName("SingleFixture_Default");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll")
                    .Containing(Suite("NUnit")
                        .Containing(Suite("Tests")
                            .Containing(Fixture(typeof(TestFixture1)).NonParallelizable()))),
                Expecting(
                    That("fake-assembly.dll").RunsOn("NonParallelWorker"),
                    That("NUnit").RunsOn("NonParallelWorker"),
                    That("Tests").RunsOn("NonParallelWorker"),
                    That("TestFixture1").RunsOn("NonParallelWorker"),
                    That("TestFixture1_Test").RunsOn("NonParallelWorker")))
                .SetName("SingleFixture_NonParallelizable");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll")
                    .Containing(Suite("NUnit")
                        .Containing(Suite("Tests")
                            .Containing(Fixture(typeof(TestFixture1)).Parallelizable()))),
                Expecting(
                    That("fake-assembly.dll").StartsOn("NonParallelWorker").FinishesOn("ParallelWorker"),
                    That("NUnit").StartsOn("NonParallelWorker").FinishesOn("ParallelWorker"),
                    That("Tests").StartsOn("NonParallelWorker").FinishesOn("ParallelWorker"),
                    That("TestFixture1").RunsOn("ParallelWorker"),
                    That("TestFixture1_Test").RunsOn("ParallelWorker")))
                .SetName("SingleFixture_Parallelizable");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll").NonParallelizable()
                    .Containing(Suite("NUnit")
                        .Containing(Suite("Tests")
                            .Containing(Fixture(typeof(TestFixture1))))),
                Expecting(
                    That("fake-assembly.dll").RunsOn("NonParallelWorker"),
                    That("NUnit").RunsOn("NonParallelWorker"),
                    That("Tests").RunsOn("NonParallelWorker"),
                    That("TestFixture1").RunsOn("NonParallelWorker"),
                    That("TestFixture1_Test").RunsOn("NonParallelWorker")))
                .SetName("SingleFixture_AssemblyNonParallelizable");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll").Parallelizable()
                    .Containing(Suite("NUnit")
                        .Containing(Suite("Tests")
                            .Containing(Fixture(typeof(TestFixture1))))),
                Expecting(
                    That("fake-assembly.dll").StartsOn("ParallelWorker").FinishesOn("NonParallelWorker"),
                    That("NUnit").StartsOn("ParallelWorker").FinishesOn("NonParallelWorker"),
                    That("Tests").StartsOn("ParallelWorker").FinishesOn("NonParallelWorker"),
                    That("TestFixture1").RunsOn("NonParallelWorker"),
                    That("TestFixture1_Test").RunsOn("NonParallelWorker")))
                .SetName("SingleFixture_AssemblyParallelizable");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll").Parallelizable()
                    .Containing(Suite("NUnit")
                        .Containing(Suite("Tests")
                            .Containing(Fixture(typeof(TestFixture1)).Parallelizable()))),
                Expecting(
                    That("fake-assembly.dll").RunsOn("ParallelWorker"),
                    That("NUnit").RunsOn("ParallelWorker"),
                    That("Tests").RunsOn("ParallelWorker"),
                    That("TestFixture1").RunsOn("ParallelWorker"),
                    That("TestFixture1_Test").RunsOn("ParallelWorker")))
                .SetName("SingleFixture_AssemblyAndFixtureParallelizable");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll")
                    .Containing(Suite("NUnit")
                        .Containing(Suite("TestData")
                            .Containing(Fixture(typeof(TestSetUpFixture))
                                .Containing(
                                    Fixture(typeof(TestFixture1)),
                                    Fixture(typeof(TestFixture2)),
                                    Fixture(typeof(TestFixture3)))))),
                Expecting(
                    That("fake-assembly.dll").RunsOn("NonParallelWorker"),
                    That("NUnit").RunsOn("NonParallelWorker"),
                    That("TestData").RunsOn("NonParallelWorker"),
                    That("ParallelExecutionData").RunsOn("NonParallelWorker"), // TestSetUpFixture
                    That("TestFixture1").RunsOn("NonParallelWorker"),
                    That("TestFixture1_Test").RunsOn("NonParallelWorker"),
                    That("TestFixture2").RunsOn("NonParallelWorker"),
                    That("TestFixture2_Test").RunsOn("NonParallelWorker"),
                    That("TestFixture3").RunsOn("NonParallelWorker"),
                    That("TestFixture3_Test").RunsOn("NonParallelWorker")))
                .SetName("ThreeFixtures_SetUpFixture_Default");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll")
                    .Containing(Suite("NUnit")
                        .Containing(Suite("TestData")
                            .Containing(Fixture(typeof(TestSetUpFixture))
                                .Containing(
                                    Fixture(typeof(TestFixture1)).Parallelizable(),
                                    Fixture(typeof(TestFixture2)),
                                    Fixture(typeof(TestFixture3)).Parallelizable())))),
                Expecting(
                    That("fake-assembly.dll").RunsOn("NonParallelWorker"),
                    That("NUnit").RunsOn("NonParallelWorker"),
                    That("TestData").RunsOn("NonParallelWorker"),
                    That("ParallelExecutionData").RunsOn("NonParallelWorker"), // TestSetUpFixture
                    That("TestFixture1").RunsOn("ParallelWorker"),
                    That("TestFixture1_Test").RunsOn("ParallelWorker"),
                    That("TestFixture2").RunsOn("NonParallelWorker"),
                    That("TestFixture2_Test").RunsOn("NonParallelWorker"),
                    That("TestFixture3").RunsOn("ParallelWorker"),
                    That("TestFixture3_Test").RunsOn("ParallelWorker")))
                .SetName("ThreeFixtures_TwoParallelizable_SetUpFixture");

            yield return new TestFixtureData(
                Suite("fake-assembly.dll")
                    .Containing(Suite("NUnit")
                        .Containing(Suite("TestData")
                            .Containing(Fixture(typeof(TestSetUpFixture)).Parallelizable()
                                .Containing(
                                    Fixture(typeof(TestFixture1)).Parallelizable(),
                                    Fixture(typeof(TestFixture2)),
                                    Fixture(typeof(TestFixture3)).Parallelizable())))),
                Expecting(
                    That("fake-assembly.dll").StartsOn("NonParallelWorker").FinishesOn("ParallelWorker"),
                    That("NUnit").StartsOn("NonParallelWorker").FinishesOn("ParallelWorker"),
                    That("TestData").StartsOn("NonParallelWorker").FinishesOn("ParallelWorker"),
                    That("ParallelExecutionData").RunsOn("ParallelWorker"), // TestSetUpFixture
                    That("TestFixture1").RunsOn("ParallelWorker"),
                    That("TestFixture1_Test").RunsOn("ParallelWorker"),
                    That("TestFixture2").RunsOn("NonParallelWorker"),
                    That("TestFixture2_Test").RunsOn("NonParallelWorker"),
                    That("TestFixture3").RunsOn("ParallelWorker"),
                    That("TestFixture3_Test").RunsOn("ParallelWorker")))
                .SetName("ThreeFixtures_TwoParallelizable_ParallelizableSetUpFixture");
        }

        #endregion

        #region ITestListener implementation

        public void TestStarted(ITest test)
        {
            _events.Enqueue(new TestEvent()
            {
                Action = TestAction.TestStarting,
                TestName = test.Name,
                ThreadName = Thread.CurrentThread.Name
            });
        }

        public void TestFinished(ITestResult result)
        {
            _events.Enqueue(new TestEvent()
            {
                Action = TestAction.TestFinished,
                TestName = result.Name,
                Result = result.ResultState.ToString(),
                ThreadName = Thread.CurrentThread.Name
            });
        }

        public void TestOutput(TestOutput output)
        {

        }

        #endregion

        #region Helper Methods

        private static TestSuite Suite(string name)
        {
            return TestBuilder.MakeSuite(name);
        }

        private static TestSuite Fixture(Type type)
        {
            return TestBuilder.MakeFixture(type);
        }

        private static Expectations Expecting(params Expectation[] expectations)
        {
            return new Expectations(expectations);
        }

        private static Expectation That(string TestName)
        {
            return new Expectation(TestName, null);
        }

        private string DumpEvents(string message)
        {
            var sb = new StringBuilder().AppendLine(message);

            foreach (var e in _events)
                sb.AppendLine(e.ToString());

            return sb.ToString();
        }

        #endregion

        #region Nested Types

        public enum TestAction
        {
            ShiftStarted,
            ShiftFinished,
            TestStarting,
            TestFinished
        }

        public class TestEvent
        {
            public TestAction Action;
            public string TestName;
            public string ThreadName;
            public string ShiftName;
            public string Result;

            public override string ToString()
            {
                switch (Action)
                {
                    case TestAction.ShiftStarted:
                        return $"{Action} {ShiftName}";

                    default:
                    case TestAction.TestStarting:
                        return $"{Action} {TestName} [{ThreadName}]";

                    case TestAction.TestFinished:
                        return $"{Action} {TestName} {Result} [{ThreadName}]";
                }
            }
        }

        public class Expectation
        {
            public string TestName { get; }
            public string StartWorker { get; private set; }
            public string FinishWorker { get; private set; }

            public Expectation(string testName, string workerType)
            {
                TestName = testName;
                StartWorker = workerType;
            }

            public Expectation RunsOn(string worker)
            {
                StartWorker = FinishWorker = worker;
                return this;
            }

            public Expectation StartsOn(string worker)
            {
                StartWorker = worker;
                return this;
            }

            public Expectation FinishesOn(string worker)
            {
                FinishWorker = worker;
                return this;
            }

            public void Verify(TestEvent e)
            {
                var worker = e.Action == TestAction.TestStarting ? StartWorker : FinishWorker;
                Assert.That(e.ThreadName, Does.StartWith(worker), $"{e.Action} {e.TestName} running on wrong type of worker thread.");
            }
        }

        public class Expectations
        {
            private Dictionary<string, Expectation> _expectations = new Dictionary<string, Expectation>();

            public Expectations(params Expectation[] expectations)
            {
                foreach (var expectation in expectations)
                    _expectations.Add(expectation.TestName, expectation);
            }

            public void Verify(TestEvent e)
            {
                Assert.That(_expectations, Does.ContainKey(e.TestName), $"The test {e.TestName} is not in the dictionary.");
                _expectations[e.TestName].Verify(e);
            }
        }

        #endregion
    }
}
#endif
