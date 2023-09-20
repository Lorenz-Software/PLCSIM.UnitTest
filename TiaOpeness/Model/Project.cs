using System.IO;

namespace TiaOpeness.Model
{
    public class Project
    {
        #region properties

        public string Name
        {
            get;
            set;
        }

        public DirectoryInfo TargetDirectory
        {
            get;
            set;
        }

        #endregion
    }
}
