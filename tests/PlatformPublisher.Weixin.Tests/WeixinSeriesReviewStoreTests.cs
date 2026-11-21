using PlatformPublisher.Weixin.Publishing;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinSeriesReviewStoreTests
{
    [Fact]
    public void Review_record_survives_reopening_and_is_scoped_to_account_and_project()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var project = Path.Combine(directory.FullName, "project");
            var data = Path.Combine(directory.FullName, "state");
            new WeixinSeriesReviewStore(data).RecordReachedReview("account-one", project);

            var reopened = new WeixinSeriesReviewStore(data);
            Assert.True(reopened.HasReachedReview("account-one", project + Path.DirectorySeparatorChar));
            Assert.False(reopened.HasReachedReview("account-two", project));
            Assert.False(reopened.HasReachedReview("account-one", Path.Combine(directory.FullName, "other")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
