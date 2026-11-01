import os
import sys
import threading
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import ataxx_gpu as G  # noqa: E402


class _Mem:
    def __init__(self, used, total):
        self.used, self.total = used, total


class _Util:
    def __init__(self, gpu):
        self.gpu = gpu


class FakeNvml:
    NVML_TEMPERATURE_GPU = 0

    def __init__(self, fail_init=False, fail_after=None, no_sensors=False):
        self.fail_init, self.fail_after, self.no_sensors = fail_init, fail_after, no_sensors
        self.reads = 0
        self.shutdowns = 0
        self.util_values = [10, 90, 50]

    def nvmlInit(self):
        if self.fail_init:
            raise RuntimeError("driver not loaded")

    def nvmlShutdown(self):
        self.shutdowns += 1

    def nvmlDeviceGetHandleByIndex(self, i):
        return i

    def nvmlDeviceGetName(self, h):
        return b"Fake GPU"

    def nvmlDeviceGetMemoryInfo(self, h):
        self.reads += 1
        if self.fail_after is not None and self.reads > self.fail_after:
            raise RuntimeError("lost device")
        return _Mem((1000 + 100 * self.reads) * 1048576, 8000 * 1048576)

    def nvmlDeviceGetUtilizationRates(self, h):
        return _Util(self.util_values[self.reads % 3])

    def nvmlDeviceGetTemperature(self, h, s):
        if self.no_sensors:
            raise RuntimeError("not supported")
        return 55

    def nvmlDeviceGetPowerUsage(self, h):
        if self.no_sensors:
            raise RuntimeError("not supported")
        return 120000


class GpuMonitorTests(unittest.TestCase):
    def test_interval_zero_starts_nothing(self):
        before = threading.active_count()
        m = G.GpuMonitor(0, nvml=FakeNvml())
        self.assertFalse(m.enabled)
        self.assertEqual(threading.active_count(), before)   # no thread at all
        self.assertIsNone(m.summary())
        m.close()

    def test_missing_driver_disables_without_raising(self):
        lines = []
        m = G.GpuMonitor(1, nvml=FakeNvml(fail_init=True), log=lines.append)
        self.assertFalse(m.enabled)
        self.assertIn("RuntimeError", m.disabled_reason)
        self.assertTrue(any("disabled" in l for l in lines))
        m.close()

    def test_missing_package_disables_without_raising(self):
        import builtins
        real = builtins.__import__

        def fake_import(name, *a, **k):
            if name == "pynvml":
                raise ImportError("No module named 'pynvml'")
            return real(name, *a, **k)

        builtins.__import__ = fake_import
        try:
            m = G.GpuMonitor(1, log=lambda s: None)
        finally:
            builtins.__import__ = real
        self.assertFalse(m.enabled)
        self.assertIn("ImportError", m.disabled_reason)
        m.close()

    def test_samples_summary_and_windows(self):
        fake = FakeNvml()
        lines = []
        m = G.GpuMonitor(0.01, nvml=fake, log=lines.append, print_every=1000)
        time.sleep(0.4)
        w = m.take_window()
        self.assertGreater(w["samples"], 5)
        self.assertEqual(w["util_max"], 90)
        self.assertEqual(w["temp_max"], 55)
        self.assertEqual(w["power_avg_w"], 120.0)
        self.assertGreaterEqual(w["mem_peak_mib"], 1100)
        w2 = m.take_window()                      # window resets, totals do not
        s = m.summary()
        m.close()
        self.assertGreaterEqual(s["samples"], w["samples"])
        self.assertEqual(s["total_mib"], 8000)
        self.assertEqual(s["device"], "Fake GPU")
        self.assertEqual(fake.shutdowns, 1)

    def test_read_failure_stops_sampling_quietly(self):
        fake = FakeNvml(fail_after=3)
        m = G.GpuMonitor(0.01, nvml=fake, log=lambda s: None)
        time.sleep(0.4)
        self.assertFalse(m.enabled)
        self.assertIn("NVML read failed", m.disabled_reason)
        m.close()
        self.assertEqual(fake.shutdowns, 1)

    def test_optional_sensors_may_be_missing(self):
        m = G.GpuMonitor(0.01, nvml=FakeNvml(no_sensors=True), log=lambda s: None)
        time.sleep(0.2)
        s = m.summary()
        m.close()
        self.assertEqual(s["temp_max"], 0)
        self.assertEqual(s["power_max_w"], 0.0)
        self.assertGreater(s["samples"], 0)

    def test_periodic_line_is_printed(self):
        lines = []
        m = G.GpuMonitor(0.01, nvml=FakeNvml(), log=lines.append, print_every=3)
        time.sleep(0.3)
        m.close()
        self.assertTrue(any("util" in l and "MiB" in l for l in lines))
        self.assertTrue(any("summary" in l for l in lines))


class TrainIntegration(unittest.TestCase):
    def test_gpu_log_on_cpu_run_is_a_noop(self):
        import json, random, shutil, tempfile
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import helpers as H
        import train as T
        d = tempfile.mkdtemp(prefix="attax_gpu_")
        try:
            rng = random.Random(3)
            H.write_log(os.path.join(d, "a.bin"), [H.make_game(u, rng, n=6, scores=lambda ply: 1_000_000) for u in range(20)],
                        [{"id": 1, "kind": 0, "depth": 3, "node_budget": 0, "root_bonus_scale": 1.0}])
            rep = T.main(["--data", os.path.join(d, "a.bin"), "--out", os.path.join(d, "o"), "--epochs", "1", "--device", "cpu",
                          "--channels", "8", "--layers", "2", "--no-validate-onnx", "--gpu-log", "1"])
            self.assertIsNone(rep["gpu_summary"])
            self.assertEqual(rep["torch_memory"], {})
            self.assertNotIn("gpu", rep["history"][0])
        finally:
            shutil.rmtree(d, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
