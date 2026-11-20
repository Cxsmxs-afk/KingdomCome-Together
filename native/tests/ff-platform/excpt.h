// Fixture inputs point into valid local objects. No Windows SEH on Linux.
#pragma once
#define __try try
#define __except(filter) catch (...)
