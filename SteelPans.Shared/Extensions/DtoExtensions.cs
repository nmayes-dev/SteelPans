using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using System.Linq.Expressions;

public static class DtoExtensions
{
    public static bool CanManage(this GroupSummaryDto? self)
    {
        return self is not null && (self.Role == GroupRole.Leader || self.Role == GroupRole.Admin);
    }
}