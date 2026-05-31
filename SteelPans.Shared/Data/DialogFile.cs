using Microsoft.AspNetCore.Components.Forms;
using System;
using System.Collections.Generic;
using System.Text;

namespace SteelPans.Shared.Data;


public sealed class DialogFile
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public IBrowserFile? File { get; set; }
}
