using NUnit.Framework;
using System;
using System.Runtime.InteropServices; // 必须引入这个命名空间

namespace Cad.Tests.Unit
{
    /// <summary>
    /// 这是一个模拟的业务类（纯 C# 逻辑，不依赖 CAD API）
    /// </summary>
    public class Calculator
    {
        public int Add(int a, int b)
        {
            return a + b;
        }

        public int Divide(int a, int b)
        {
            if (b == 0) throw new DivideByZeroException("除数不能为0！");
            return a / b;
        }
    }

    // ================= 测试类 =================
    [TestFixture]
    public class CalculatorTests
    {
        private Calculator _calc;

        private void InvokeDivideByZero()
        {
            _calc.Divide(10, 0);
        }

        [SetUp]
        public void Setup()
        {
            // 【生命周期 1】在每个 [Test] 方法运行前，自动执行这里
            _calc = new Calculator();
            Console.WriteLine("1. -> SetUp 执行：初始化了 Calculator 实例");
        }

        [TearDown]
        public void TearDown()
        {
            // 【生命周期 3】在每个 [Test] 方法运行后（无论成功失败），自动执行这里
            _calc = null;
            Console.WriteLine("3. -> TearDown 执行：清理了资源\n");
        }

        // ================= 测试用例 1：正常情况 =================
        [Test]
        public void Add_TwoPositiveNumbers_ReturnsCorrectSum()
        {
            Console.WriteLine("2. -> 正在执行 Add 测试...");
            
            // Act (执行动作)
            int result = _calc.Add(5, 7);
            
            // Assert (断言：验证结果是不是我们期望的 12)
            Assert.That(result, Is.EqualTo(12)); 
        }

        // ================= 测试用例 2：异常情况 =================
        [Test]
        public void Divide_ByZero_ThrowsException()
        {
            Console.WriteLine("2. -> 正在执行 Divide 异常测试...");

            // Assert.That 配合 Throws 可以验证代码是否如期抛出了异常
            Assert.Throws<DivideByZeroException>((Action)(() => _calc.Divide(10, 0)));
        }

        [Test]
        public void CheckCurrentFramework()
        {
            // 这行代码会获取当前代码实际运行的 .NET 框架描述
            string currentFramework = RuntimeInformation.FrameworkDescription;
            
            // 把它打印到测试输出窗口
            Console.WriteLine($"👉 当前测试实际运行的框架是: {currentFramework}");
            
            // 顺便做一个断言，确保它包含 "Framework" (代表 .NET Framework) 而不是 ".NET Core"
            Assert.That(currentFramework, Does.Contain("Framework"));
        }
    }
}