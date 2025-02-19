using System;

namespace Services.AddressablesService.Exceptions
{
    public class InexistentAssetWithLabelException : Exception
    {
        public InexistentAssetWithLabelException(string label) : base(label)
        {
        }
    }
}