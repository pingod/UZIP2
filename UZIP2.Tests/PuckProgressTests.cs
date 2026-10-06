using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 迷你方块的进度环：进度 0~100% 映射为绕图标 0~360°，转完一整圈就是 100%
    public class PuckProgressTests
    {
        const double Size = 96;
        const double Thickness = 5;
        const double Center = Size / 2;
        const double Radius = Size / 2 - Thickness / 2;   // 44.5：描边居中，环不外溢

        static JobEntry Job(JobStatus status, double? percent = null)
            => new JobEntry { Status = status, Percent = percent };

        // 角度从 12 点起顺时针，弧终点坐标的唯一算法（测试独立复算，不借实现代码）
        static Point ExpectedPoint(double percent)
        {
            double a = percent * 3.6 * Math.PI / 180.0;
            return new Point(Center + Radius * Math.Sin(a), Center - Radius * Math.Cos(a));
        }

        static PathFigure FirstFigure(double? percent)
            => PuckProgress.RingGeometry(percent, Size, Thickness).Figures.First();

        // ---------- SweepAngle：百分比 -> 角度 ----------

        [Theory]
        [InlineData(null, 0.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(25.0, 90.0)]
        [InlineData(50.0, 180.0)]
        [InlineData(75.0, 270.0)]
        [InlineData(100.0, 360.0)]
        public void SweepAngle_maps_percent_onto_one_full_turn(double? percent, double expected)
            => Assert.Equal(expected, PuckProgress.SweepAngle(percent), 6);

        [Theory]
        [InlineData(-10.0)]
        [InlineData(480.0)]
        public void SweepAngle_clamps_out_of_range(double percent)
            => Assert.InRange(PuckProgress.SweepAngle(percent), 0.0, 360.0);

        // ---------- Overall：一批作业的总进度 ----------

        [Fact]
        public void Overall_is_null_when_nothing_is_active()
            => Assert.Null(PuckProgress.Overall(new[] { Job(JobStatus.Success, 100), Job(JobStatus.Failed, 40) }));

        [Fact]
        public void Overall_is_null_for_empty_queue()
            => Assert.Null(PuckProgress.Overall(Array.Empty<JobEntry>()));

        [Fact]
        public void Overall_counts_queued_as_zero()
        {
            var overall = PuckProgress.Overall(new[] { Job(JobStatus.Queued) });
            Assert.NotNull(overall);
            Assert.Equal(0.0, overall.Value, 6);
        }

        [Fact]
        public void Overall_averages_active_jobs()
        {
            var jobs = new[] { Job(JobStatus.Running, 100), Job(JobStatus.Queued) };
            Assert.Equal(50.0, PuckProgress.Overall(jobs).GetValueOrDefault(-1), 6);
        }

        [Fact]
        public void Overall_ignores_terminal_jobs()
        {
            var jobs = new[] { Job(JobStatus.Running, 60), Job(JobStatus.Success, 100), Job(JobStatus.Cancelled, 5) };
            Assert.Equal(60.0, PuckProgress.Overall(jobs).GetValueOrDefault(-1), 6);
        }

        [Fact]
        public void Overall_treats_unknown_percent_as_zero()
        {
            var jobs = new[] { Job(JobStatus.Running, null), Job(JobStatus.Running, 80) };
            Assert.Equal(40.0, PuckProgress.Overall(jobs).GetValueOrDefault(-1), 6);
        }

        [Fact]
        public void Overall_clamps_a_runaway_percent()
            => Assert.Equal(100.0, PuckProgress.Overall(new[] { Job(JobStatus.Running, 250) }).GetValueOrDefault(-1), 6);

        // ---------- Geometry：环的画法 ----------

        [Fact]
        public void Ring_starts_at_twelve_oclock()
        {
            var start = FirstFigure(25).StartPoint;
            Assert.Equal(Center, start.X, 6);
            Assert.Equal(Center - Radius, start.Y, 6);
        }

        [Theory]
        [InlineData(25.0)]
        [InlineData(50.0)]
        [InlineData(75.0)]
        public void Ring_ends_where_the_matching_angle_lands(double percent)
        {
            var figs = PuckProgress.RingGeometry(percent, Size, Thickness).Figures;
            var last = figs.First().Segments.OfType<ArcSegment>().Last();
            var expected = ExpectedPoint(percent);
            Assert.Equal(expected.X, last.Point.X, 4);
            Assert.Equal(expected.Y, last.Point.Y, 4);
        }

        [Fact]
        public void Ring_arcs_go_clockwise_and_use_the_right_radius()
        {
            foreach (double p in new[] { 10.0, 50.0, 90.0 })
            {
                var arc = FirstFigure(p).Segments.OfType<ArcSegment>().First();
                Assert.Equal(SweepDirection.Clockwise, arc.SweepDirection);
                Assert.Equal(Radius, arc.Size.Width, 6);
                Assert.Equal(Radius, arc.Size.Height, 6);
                Assert.Equal(p > 50, arc.IsLargeArc);
            }
        }

        [Fact]
        public void Full_percent_closes_the_ring_into_a_complete_turn()
        {
            var fig = FirstFigure(100);
            var arcs = fig.Segments.OfType<ArcSegment>().ToList();
            Assert.Equal(2, arcs.Count);            // 一整圆无法用一段弧表达，必须两段
            Assert.DoesNotContain(arcs, a => a.IsLargeArc);
            // 半程落在 6 点，收尾落回 12 点，视觉上没有任何缺口
            var half = ExpectedPoint(50);
            Assert.Equal(half.X, arcs[0].Point.X, 4);
            Assert.Equal(half.Y, arcs[0].Point.Y, 4);
            Assert.Equal(fig.StartPoint.X, arcs[1].Point.X, 4);
            Assert.Equal(fig.StartPoint.Y, arcs[1].Point.Y, 4);
        }

        [Fact]
        public void Zero_or_unknown_percent_draws_nothing()
        {
            foreach (double? p in new double?[] { null, 0 })
            {
                var geo = PuckProgress.RingGeometry(p, Size, Thickness);
                Assert.True(geo == null || geo.Figures.Count == 0, $"进度 {p} 不该画出环");
            }
        }

        [Fact]
        public void Ring_stays_inside_its_box()
        {
            foreach (double p in new[] { 5.0, 33.0, 66.0, 99.0, 100.0 })
            {
                var geo = PuckProgress.RingGeometry(p, Size, Thickness);
                var bounds = geo.GetRenderBounds(new Pen(Brushes.Black, Thickness));
                Assert.True(bounds.X >= -0.01 && bounds.Y >= -0.01 &&
                            bounds.Right <= Size + 0.01 && bounds.Bottom <= Size + 0.01,
                            $"进度 {p}% 的环越界：{bounds}");
            }
        }
    }
}
