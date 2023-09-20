using System;
using System.ComponentModel.Composition.Hosting;

namespace ApplicationUtilities.DI
{
    public abstract class Context
    {
        protected static Context _instance = null;
        protected CompositionContainer _container = null;

        public static Context Instance
        {
            get
            {
                if (_instance == null)
                    throw new InvalidOperationException("Application context not configured");
                return _instance;
            }
            set
            {
                _instance = value;
            }
        }

        public static CompositionContainer Container { get => Instance._container; }

        public static T Get<T>()
        {
            return Container.GetExportedValue<T>();
        }
    }
}
