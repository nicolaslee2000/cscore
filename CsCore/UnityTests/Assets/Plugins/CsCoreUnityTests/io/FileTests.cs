﻿using NUnit.Framework;
using Zio;

namespace com.csutil.tests.io {

    public class FileTests {

        [SetUp]
        public void BeforeEachTest() { }

        [TearDown]
        public void AfterEachTest() { }

        [Test]
        public void TestFilesWithEnumeratorPasses() {
            var dir = EnvironmentV2.instance.GetCurrentDirectory();
            Log.d("dir=" + dir.FullName);
            Assert.IsNotEmpty(dir.FullName);
            dir = EnvironmentV2.instance.GetRootAppDataFolder();
            Log.d("dir=" + dir.FullName);
            Assert.IsNotEmpty(dir.FullName);
        }

    }

}