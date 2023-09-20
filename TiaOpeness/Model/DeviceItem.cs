namespace TiaOpeness.Model
{
    public class DeviceItem
    {
        #region properties

        public string Name
        {
            get;
            set;
        }

        public string DeviceName
        {
            get;
            set;
        }

        public string Classification
        {
            get;
            set;
        }

        public override string ToString()
        {
            return $"Name={Name}, DeviceName={DeviceName}, Classification={Classification}";
        }

        #endregion // properties


    }
}
