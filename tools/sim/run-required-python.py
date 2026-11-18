#!/usr/bin/env python3
# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.

import sys
import unittest


class RequiredTestResult(unittest.TextTestResult):
    def wasSuccessful(self) -> bool:
        return super().wasSuccessful() and self.testsRun > 0 and not self.skipped


class RequiredTestRunner(unittest.TextTestRunner):
    resultclass = RequiredTestResult


if __name__ == "__main__":
    tests = unittest.main(module=None, testRunner=RequiredTestRunner, exit=False)
    sys.exit(0 if tests.result.wasSuccessful() else 1)
