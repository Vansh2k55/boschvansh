using Xunit;

// Several tests set and clear GROQ_API_KEY to exercise the "cloud configured"
// and "no cloud key" paths. That variable is process-wide, so running classes
// in parallel lets one test's setup change another's provider selection
// mid-flight. Serialising the assembly is the honest fix; these tests are
// bounded by stubbed transports, not by real network calls.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
