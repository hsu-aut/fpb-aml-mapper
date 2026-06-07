using Xunit;

// Aml.Engine maintains static caches (XDocumentWrapper.CheckReferences enumerates a
// shared dictionary) that race when multiple CAEXDocument instances are built in
// parallel. Run all tests in this assembly sequentially to avoid intermittent
// "Collection was modified" failures originating from Aml.Engine internals.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
