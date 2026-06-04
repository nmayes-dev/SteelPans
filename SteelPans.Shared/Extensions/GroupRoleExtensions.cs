using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using System.Linq.Expressions;

public static class GroupRoleExtensions
{
    public static Expression<Func<EnsembleGroupMember, int>> RoleSortOrder =>
        member => member.Role == GroupRole.Leader ? 0 :
                  member.Role == GroupRole.Admin ? 10 :
                  member.Role == GroupRole.Member ? 100 :
                  int.MaxValue;
}